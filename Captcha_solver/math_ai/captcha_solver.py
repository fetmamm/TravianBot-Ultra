from __future__ import annotations

import argparse
import os
import re
from dataclasses import dataclass, field
from typing import Iterable

import cv2
import numpy as np
import sympy as sp
import tensorflow as tf


IMAGE_EXTENSIONS = (".png", ".jpg", ".jpeg", ".bmp", ".webp")


@dataclass
class SolverConfig:
    debug: bool = True
    max_region_candidates: int = 12
    review_confidence: float = 98.0
    image_size: int = 64
    min_region_area_ratio: float = 0.01
    max_region_area_ratio: float = 0.65
    min_region_aspect_ratio: float = 1.8
    max_region_aspect_ratio: float = 10.0
    min_fill_ratio: float = 0.015
    max_fill_ratio: float = 0.55
    max_boxes_per_region: int = 12
    min_symbol_height_ratio: float = 0.1
    min_symbol_width_ratio: float = 0.02
    max_symbol_width_ratio: float = 0.42
    min_symbol_area: int = 18
    question_gap_ratio: float = 0.8
    min_candidate_confidence: float = 80.0
    output_review_dir: str = "review_dataset"


@dataclass
class SymbolPrediction:
    symbol: str
    confidence: float
    box: tuple[int, int, int, int]
    normalized: np.ndarray


@dataclass
class ParsedExpression:
    expression: str
    answer: str
    used_count: int
    average_confidence: float
    valid: bool


@dataclass
class CandidateResult:
    region_box: tuple[int, int, int, int]
    region_image: np.ndarray
    threshold_name: str
    threshold_image: np.ndarray
    symbols: list[SymbolPrediction]
    parsed: ParsedExpression
    score: float
    reason: str = ""


def is_image_file(filename: str) -> bool:
    return filename.lower().endswith(IMAGE_EXTENSIONS)


def safe_stem(path: str) -> str:
    stem = os.path.splitext(os.path.basename(path))[0]
    return re.sub(r"[^a-zA-Z0-9_-]+", "_", stem).strip("_") or "image"


def read_image(path: str, flags: int = cv2.IMREAD_COLOR) -> np.ndarray | None:
    try:
        data = np.fromfile(path, dtype=np.uint8)
    except OSError:
        return None

    if data.size == 0:
        return None

    return cv2.imdecode(data, flags)


def load_class_names(path: str = "classes.txt") -> list[str]:
    with open(path, "r", encoding="utf-8") as file:
        return [line.strip() for line in file.readlines() if line.strip()]


def normalize_character_image(image: np.ndarray, size: int = 64) -> np.ndarray:
    if image.ndim == 3:
        image = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)

    if image.dtype != np.uint8:
        image = np.clip(image, 0, 255).astype(np.uint8)

    if np.count_nonzero(image) == 0:
        return np.zeros((size, size), dtype=np.uint8)

    ys, xs = np.where(image > 0)
    x1, x2 = xs.min(), xs.max()
    y1, y2 = ys.min(), ys.max()
    cropped = image[y1:y2 + 1, x1:x2 + 1]

    height, width = cropped.shape
    canvas_size = max(height, width) + max(6, int(max(height, width) * 0.4))
    canvas = np.zeros((canvas_size, canvas_size), dtype=np.uint8)

    offset_y = (canvas_size - height) // 2
    offset_x = (canvas_size - width) // 2
    canvas[offset_y:offset_y + height, offset_x:offset_x + width] = cropped

    resized = cv2.resize(canvas, (size, size), interpolation=cv2.INTER_NEAREST)
    return resized


def build_threshold_variants(gray: np.ndarray) -> dict[str, np.ndarray]:
    blurred = cv2.GaussianBlur(gray, (3, 3), 0)
    equalized = cv2.equalizeHist(blurred)

    variants: dict[str, np.ndarray] = {}

    _, otsu = cv2.threshold(
        blurred,
        0,
        255,
        cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU
    )
    variants["otsu"] = otsu

    _, otsu_eq = cv2.threshold(
        equalized,
        0,
        255,
        cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU
    )
    variants["otsu_equalized"] = otsu_eq

    adaptive_mean = cv2.adaptiveThreshold(
        equalized,
        255,
        cv2.ADAPTIVE_THRESH_MEAN_C,
        cv2.THRESH_BINARY_INV,
        31,
        8
    )
    variants["adaptive_mean"] = adaptive_mean

    adaptive_gaussian = cv2.adaptiveThreshold(
        equalized,
        255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY_INV,
        31,
        8
    )
    variants["adaptive_gaussian"] = adaptive_gaussian

    return variants


def expand_box(
    box: tuple[int, int, int, int],
    image_shape: tuple[int, int],
    pad_x: int,
    pad_y: int
) -> tuple[int, int, int, int]:
    height, width = image_shape[:2]
    x, y, w, h = box
    x1 = max(0, x - pad_x)
    y1 = max(0, y - pad_y)
    x2 = min(width, x + w + pad_x)
    y2 = min(height, y + h + pad_y)
    return x1, y1, x2 - x1, y2 - y1


def compute_iou(box_a: tuple[int, int, int, int], box_b: tuple[int, int, int, int]) -> float:
    ax, ay, aw, ah = box_a
    bx, by, bw, bh = box_b

    left = max(ax, bx)
    top = max(ay, by)
    right = min(ax + aw, bx + bw)
    bottom = min(ay + ah, by + bh)

    if right <= left or bottom <= top:
        return 0.0

    intersection = (right - left) * (bottom - top)
    union = aw * ah + bw * bh - intersection
    return intersection / union if union else 0.0


def deduplicate_boxes(boxes: Iterable[tuple[int, int, int, int]]) -> list[tuple[int, int, int, int]]:
    unique: list[tuple[int, int, int, int]] = []

    for box in sorted(boxes, key=lambda item: item[2] * item[3], reverse=True):
        if any(compute_iou(box, existing) > 0.6 for existing in unique):
            continue
        unique.append(box)

    return unique


def remove_nested_boxes(boxes: list[tuple[int, int, int, int]]) -> list[tuple[int, int, int, int]]:
    filtered: list[tuple[int, int, int, int]] = []

    for index, box in enumerate(boxes):
        x, y, w, h = box
        is_nested = False

        for other_index, other in enumerate(boxes):
            if index == other_index:
                continue

            ox, oy, ow, oh = other
            if x >= ox and y >= oy and x + w <= ox + ow and y + h <= oy + oh:
                if ow * oh > w * h:
                    is_nested = True
                    break

        if not is_nested:
            filtered.append(box)

    return filtered


def split_wide_symbol_boxes(
    boxes: list[tuple[int, int, int, int]],
    threshold_image: np.ndarray,
) -> list[tuple[int, int, int, int]]:
    if len(boxes) < 2:
        return boxes

    widths = [box[2] for box in boxes]
    base_width = float(np.median(widths))
    if base_width <= 0:
        return boxes

    split_boxes: list[tuple[int, int, int, int]] = []

    for x, y, w, h in boxes:
        should_split = (
            w >= max(14, int(base_width * 1.6))
            and w <= int(base_width * 2.8)
            and h >= 8
        )

        if not should_split:
            split_boxes.append((x, y, w, h))
            continue

        crop = threshold_image[y:y + h, x:x + w]
        projection = np.count_nonzero(crop, axis=0)
        left = max(3, int(w * 0.35))
        right = min(w - 3, int(w * 0.65))

        if left < right:
            split_at = left + int(np.argmin(projection[left:right]))
        else:
            split_at = w // 2

        if split_at < 4 or w - split_at < 4:
            split_at = w // 2

        split_boxes.append((x, y, split_at, h))
        split_boxes.append((x + split_at, y, w - split_at, h))

    return sorted(split_boxes, key=lambda item: item[0])


def try_parse_expression(expression: str) -> ParsedExpression:
    if not expression:
        return ParsedExpression("", "", 0, 0.0, False)

    if expression.count("+") + expression.count("-") != 1:
        return ParsedExpression(expression, "", 0, 0.0, False)

    operator = "+" if "+" in expression else "-"
    left, right = expression.split(operator, 1)

    if not left.isdigit() or not right.isdigit():
        return ParsedExpression(expression, "", 0, 0.0, False)

    if (len(left) > 1 and left.startswith("0")) or (len(right) > 1 and right.startswith("0")):
        return ParsedExpression(expression, "", 0, 0.0, False)

    if int(left) > 20 or int(right) > 10:
        return ParsedExpression(expression, "", 0, 0.0, False)

    try:
        answer = str(sp.sympify(expression))
    except Exception:
        return ParsedExpression(expression, "", 0, 0.0, False)

    return ParsedExpression(expression, answer, 0, 0.0, True)


class CaptchaSolver:
    def __init__(
        self,
        model_path: str = "model.keras",
        classes_path: str = "classes.txt",
        config: SolverConfig | None = None,
    ) -> None:
        self.config = config or SolverConfig()
        self.model = tf.keras.models.load_model(model_path)
        self.class_names = load_class_names(classes_path)

    def classify_character(self, char_image: np.ndarray) -> SymbolPrediction:
        normalized = normalize_character_image(char_image, size=self.config.image_size)
        input_img = normalized.astype("float32")
        input_img = np.expand_dims(input_img, axis=-1)
        input_img = np.expand_dims(input_img, axis=0)

        prediction = self.model.predict(input_img, verbose=0)[0]
        index = int(np.argmax(prediction))
        confidence = float(np.max(prediction) * 100.0)

        return SymbolPrediction(
            symbol=self.class_names[index],
            confidence=confidence,
            box=(0, 0, normalized.shape[1], normalized.shape[0]),
            normalized=normalized,
        )

    def find_panel_regions(self, gray: np.ndarray) -> list[tuple[int, int, int, int]]:
        mask = cv2.inRange(gray, 225, 245)
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel, iterations=1)

        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        image_area = gray.shape[0] * gray.shape[1]
        boxes: list[tuple[int, int, int, int]] = []

        for contour in contours:
            x, y, w, h = cv2.boundingRect(contour)
            area_ratio = (w * h) / max(image_area, 1)
            aspect_ratio = w / max(h, 1)

            if y < gray.shape[0] * 0.16:
                continue
            if area_ratio < 0.008 or area_ratio > 0.06:
                continue
            if aspect_ratio < 2.4 or aspect_ratio > 6.5:
                continue
            if w < 80 or h < 25:
                continue

            boxes.append(expand_box((x, y, w, h), gray.shape, pad_x=4, pad_y=4))

        return boxes

    def find_expression_regions(self, image: np.ndarray) -> list[tuple[int, int, int, int]]:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        variants = build_threshold_variants(gray)
        image_area = gray.shape[0] * gray.shape[1]
        all_boxes: list[tuple[int, int, int, int]] = self.find_panel_regions(gray)

        for variant in variants.values():
            kernel_width = max(15, gray.shape[1] // 12)
            close_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (kernel_width, 3))
            merged = cv2.morphologyEx(variant, cv2.MORPH_CLOSE, close_kernel, iterations=2)

            dilate_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 3))
            merged = cv2.dilate(merged, dilate_kernel, iterations=1)

            contours, _ = cv2.findContours(merged, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

            for contour in contours:
                x, y, w, h = cv2.boundingRect(contour)
                area_ratio = (w * h) / image_area
                aspect_ratio = w / max(h, 1)

                if area_ratio < self.config.min_region_area_ratio:
                    continue
                if area_ratio > self.config.max_region_area_ratio:
                    continue
                if aspect_ratio < self.config.min_region_aspect_ratio:
                    continue
                if aspect_ratio > self.config.max_region_aspect_ratio:
                    continue

                expanded = expand_box(
                    (x, y, w, h),
                    gray.shape,
                    pad_x=max(8, int(w * 0.08)),
                    pad_y=max(6, int(h * 0.18)),
                )

                ex, ey, ew, eh = expanded
                crop = variant[ey:ey + eh, ex:ex + ew]
                fill_ratio = np.count_nonzero(crop) / max(crop.size, 1)

                if fill_ratio < self.config.min_fill_ratio:
                    continue
                if fill_ratio > self.config.max_fill_ratio:
                    continue

                all_boxes.append(expanded)

        if not all_boxes:
            height, width = gray.shape
            fallback = (
                int(width * 0.15),
                int(height * 0.2),
                int(width * 0.7),
                int(height * 0.4),
            )
            return [fallback]

        unique = deduplicate_boxes(all_boxes)
        unique.sort(key=lambda item: item[2] * item[3], reverse=True)
        return unique[:self.config.max_region_candidates]

    def find_line_regions(self, region_image: np.ndarray) -> list[tuple[int, int, int, int]]:
        gray = cv2.cvtColor(region_image, cv2.COLOR_BGR2GRAY)
        variants = build_threshold_variants(gray)
        region_area = gray.shape[0] * gray.shape[1]
        boxes: list[tuple[int, int, int, int]] = []

        for variant in variants.values():
            kernel_width = max(12, gray.shape[1] // 8)
            close_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (kernel_width, 3))
            merged = cv2.morphologyEx(variant, cv2.MORPH_CLOSE, close_kernel, iterations=1)

            contours, _ = cv2.findContours(merged, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

            for contour in contours:
                x, y, w, h = cv2.boundingRect(contour)
                area = w * h
                area_ratio = area / max(region_area, 1)
                aspect_ratio = w / max(h, 1)

                if area_ratio < 0.01 or area_ratio > 0.5:
                    continue
                if aspect_ratio < 2.0 or aspect_ratio > 12.0:
                    continue
                if h < gray.shape[0] * 0.08 or h > gray.shape[0] * 0.55:
                    continue

                boxes.append(
                    expand_box(
                        (x, y, w, h),
                        gray.shape,
                        pad_x=max(6, int(w * 0.05)),
                        pad_y=max(4, int(h * 0.35)),
                    )
                )

        unique = deduplicate_boxes(boxes)
        unique.sort(key=lambda item: item[2] * item[3], reverse=True)
        return unique[:self.config.max_region_candidates]

    def segment_characters(
        self,
        region_image: np.ndarray,
    ) -> list[tuple[str, np.ndarray, list[tuple[int, int, int, int]]]]:
        gray = cv2.cvtColor(region_image, cv2.COLOR_BGR2GRAY)
        threshold_variants = build_threshold_variants(gray)
        segmented: list[tuple[str, np.ndarray, list[tuple[int, int, int, int]]]] = []

        region_height, region_width = gray.shape
        min_height = max(6, int(region_height * self.config.min_symbol_height_ratio))
        min_width = max(3, int(region_width * self.config.min_symbol_width_ratio))
        max_width = max(min_width + 1, int(region_width * self.config.max_symbol_width_ratio))

        for name, threshold_image in threshold_variants.items():
            contours, _ = cv2.findContours(
                threshold_image,
                cv2.RETR_EXTERNAL,
                cv2.CHAIN_APPROX_SIMPLE
            )

            boxes: list[tuple[int, int, int, int]] = []

            for contour in contours:
                x, y, w, h = cv2.boundingRect(contour)
                area = w * h

                if area < self.config.min_symbol_area:
                    continue
                if h < min_height or w < min_width:
                    continue
                if w > max_width:
                    continue
                if h > region_height * 0.98:
                    continue

                boxes.append((x, y, w, h))

            if not boxes:
                continue

            boxes = sorted(remove_nested_boxes(boxes), key=lambda item: item[0])
            boxes = split_wide_symbol_boxes(boxes, threshold_image)

            if len(boxes) > self.config.max_boxes_per_region:
                continue

            segmented.append((name, threshold_image, boxes))

        return segmented

    def parse_expression(self, predictions: list[SymbolPrediction]) -> ParsedExpression:
        if not predictions:
            return ParsedExpression("", "", 0, 0.0, False)

        widths = [box[2] for box in (item.box for item in predictions)]
        median_width = float(np.median(widths)) if widths else 0.0

        expression = ""
        used_confidences: list[float] = []
        operator_found = False
        second_number_started = False

        for index, prediction in enumerate(predictions):
            symbol = prediction.symbol
            next_gap = None

            if index + 1 < len(predictions):
                current_right = prediction.box[0] + prediction.box[2]
                next_left = predictions[index + 1].box[0]
                next_gap = next_left - current_right

            if symbol.isdigit():
                expression += symbol
                used_confidences.append(prediction.confidence)
                if operator_found:
                    second_number_started = True
            elif symbol in {"plus", "minus"}:
                if operator_found or not expression or not expression[-1].isdigit():
                    break

                expression += "+" if symbol == "plus" else "-"
                used_confidences.append(prediction.confidence)
                operator_found = True
            else:
                break

            if second_number_started and next_gap is not None:
                if next_gap > max(6.0, median_width * self.config.question_gap_ratio):
                    break

        parsed = try_parse_expression(expression)

        if used_confidences:
            parsed.used_count = len(used_confidences)
            parsed.average_confidence = float(sum(used_confidences) / len(used_confidences))

        return parsed

    def score_candidate(
        self,
        parsed: ParsedExpression,
        predictions: list[SymbolPrediction],
        region_box: tuple[int, int, int, int],
    ) -> tuple[float, str]:
        if not parsed.valid:
            return -1.0, "invalid expression"
        if parsed.average_confidence < self.config.min_candidate_confidence:
            return -1.0, "low confidence"

        score = parsed.average_confidence
        reason = "valid expression"

        expected_length = len(parsed.expression)
        extra_symbols = max(0, len(predictions) - parsed.used_count)
        score -= extra_symbols * 6.0

        if expected_length < 3:
            score -= 25.0
            reason = "too short"

        x, y, w, h = region_box
        aspect_ratio = w / max(h, 1)
        if 2.0 <= aspect_ratio <= 8.5:
            score += 5.0
        if x <= 2 or y <= 2:
            score -= 15.0

        return score, reason

    def save_review_samples(self, image_stem: str, predictions: list[SymbolPrediction]) -> None:
        for index, prediction in enumerate(predictions):
            if prediction.confidence < self.config.review_confidence:
                continue

            target_dir = os.path.join(self.config.output_review_dir, prediction.symbol)
            os.makedirs(target_dir, exist_ok=True)

            file_name = f"{image_stem}_{index}_{prediction.symbol}_{prediction.confidence:.0f}.png"
            cv2.imwrite(os.path.join(target_dir, file_name), prediction.normalized)

    def solve_image(
        self,
        image_path: str,
        debug_dir: str = "debug_output",
        max_candidates: int | None = None,
    ) -> CandidateResult | None:
        image = read_image(image_path)
        if image is None:
            return None

        if self.config.debug:
            os.makedirs(debug_dir, exist_ok=True)

        image_stem = safe_stem(image_path)
        region_boxes = self.find_expression_regions(image)
        if max_candidates is not None:
            region_boxes = region_boxes[:max_candidates]

        best_result: CandidateResult | None = None

        for region_index, region_box in enumerate(region_boxes):
            rx, ry, rw, rh = region_box
            region_image = image[ry:ry + rh, rx:rx + rw]
            line_regions = self.find_line_regions(region_image)

            if not line_regions:
                line_regions = [(0, 0, region_image.shape[1], region_image.shape[0])]

            if self.config.debug:
                cv2.imwrite(
                    os.path.join(debug_dir, f"{image_stem}_region_{region_index}.png"),
                    region_image,
                )

            for line_index, line_box in enumerate(line_regions):
                lx, ly, lw, lh = line_box
                line_image = region_image[ly:ly + lh, lx:lx + lw]
                segmented_variants = self.segment_characters(line_image)

                if self.config.debug:
                    cv2.imwrite(
                        os.path.join(debug_dir, f"{image_stem}_region_{region_index}_line_{line_index}.png"),
                        line_image,
                    )

                for variant_name, threshold_image, boxes in segmented_variants:
                    predictions: list[SymbolPrediction] = []

                    for symbol_index, (x, y, w, h) in enumerate(boxes):
                        char_image = threshold_image[y:y + h, x:x + w]
                        prediction = self.classify_character(char_image)
                        prediction.box = (x, y, w, h)
                        predictions.append(prediction)

                        if self.config.debug:
                            cv2.imwrite(
                                os.path.join(
                                    debug_dir,
                                    f"{image_stem}_r{region_index}_l{line_index}_{variant_name}_char_{symbol_index}.png",
                                ),
                                prediction.normalized,
                            )

                    parsed = self.parse_expression(predictions)
                    absolute_box = (rx + lx, ry + ly, lw, lh)
                    score, reason = self.score_candidate(parsed, predictions, absolute_box)

                    candidate = CandidateResult(
                        region_box=absolute_box,
                        region_image=line_image,
                        threshold_name=variant_name,
                        threshold_image=threshold_image,
                        symbols=predictions,
                        parsed=parsed,
                        score=score,
                        reason=reason,
                    )

                    if best_result is None or candidate.score > best_result.score:
                        best_result = candidate

                    if self.config.debug:
                        cv2.imwrite(
                            os.path.join(
                                debug_dir,
                                f"{image_stem}_r{region_index}_l{line_index}_{variant_name}_thresh.png",
                            ),
                            threshold_image,
                        )

        if best_result and best_result.parsed.valid:
            self.save_review_samples(image_stem, best_result.symbols[:best_result.parsed.used_count])

        return best_result


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", help="Path to a full captcha image")
    parser.add_argument("--folder", help="Folder with captcha images")
    parser.add_argument("--model", default="model.keras")
    parser.add_argument("--classes", default="classes.txt")
    parser.add_argument("--debug-dir", default="debug_output")
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--max-candidates", type=int, default=12)
    return parser


def print_candidate_result(filename: str, result: CandidateResult | None) -> None:
    print(f"\n--- {filename} ---")

    if result is None:
        print("No candidate found")
        return

    print("Region:", result.region_box)
    print("Threshold:", result.threshold_name)
    print("Score:", round(result.score, 2), "-", result.reason)

    for symbol in result.symbols:
        print(f"{symbol.box[0]}: {symbol.symbol} ({symbol.confidence:.2f}%)")

    print("Expression:", result.parsed.expression or "<none>")
    if result.parsed.valid:
        print("Answer:", result.parsed.answer)
    else:
        print("Answer: <invalid>")
