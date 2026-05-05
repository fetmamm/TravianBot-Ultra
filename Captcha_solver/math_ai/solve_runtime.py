import argparse
import json
import os
import sys

os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")

import cv2
import numpy as np
import sympy as sp

from captcha_solver import CaptchaSolver, SolverConfig, read_image

BASE_DIR = os.path.dirname(os.path.abspath(__file__))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", help="Absolute or relative path to captcha image")
    parser.add_argument("--model", default="model.keras")
    parser.add_argument("--classes", default="classes.txt")
    parser.add_argument("--debug-dir", default="debug_output")
    parser.add_argument("--max-candidates", type=int, default=12)
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--warmup", action="store_true")
    return parser


def resolve_local_path(path: str) -> str:
    if os.path.isabs(path):
        return path

    return os.path.join(BASE_DIR, path)


def try_solve_with_batch_heuristics(
    solver: CaptchaSolver,
    image_path: str,
    debug_dir: str,
) -> dict[str, object] | None:
    img = read_image(image_path)
    if img is None:
        return None

    safe_name = os.path.splitext(os.path.basename(image_path))[0]
    safe_name = safe_name.replace(" ", "_").replace("(", "").replace(")", "")

    gray_full = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    mask = cv2.inRange(gray_full, 220, 245)

    contours, _ = cv2.findContours(
        mask,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE,
    )

    candidate_boxes: list[tuple[int, int, int, int]] = []
    for contour in contours:
        x, y, w, h = cv2.boundingRect(contour)
        area = w * h
        if area > 2000 and w > 80 and h > 30:
            candidate_boxes.append((x, y, w, h))

    if candidate_boxes:
        x, y, w, h = max(candidate_boxes, key=lambda item: item[2] * item[3])
        crop = img[y:y + h, x:x + w]
    else:
        crop = img.copy()

    if crop.shape[0] > 180 or crop.shape[1] > 350:
        ch, cw, _ = crop.shape
        crop = crop[int(ch * 0.34):int(ch * 0.50), int(cw * 0.30):int(cw * 0.70)]

    os.makedirs(debug_dir, exist_ok=True)
    cv2.imwrite(os.path.join(debug_dir, f"{safe_name}_batch_crop.png"), crop)

    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    _, thresh = cv2.threshold(
        gray,
        0,
        255,
        cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU,
    )

    cv2.imwrite(os.path.join(debug_dir, f"{safe_name}_batch_thresh.png"), thresh)

    contours, _ = cv2.findContours(
        thresh,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE,
    )

    boxes: list[tuple[int, int, int, int]] = []
    for contour in contours:
        x, y, w, h = cv2.boundingRect(contour)
        if y < 20 or y > 90:
            continue
        if w < 4 or h < 6:
            continue
        if h <= 4:
            continue
        boxes.append((x, y, w, h))

    boxes = sorted(boxes, key=lambda item: item[0])

    filtered_boxes: list[tuple[int, int, int, int]] = []
    for x, y, w, h in boxes:
        if h <= 6 and w >= 15:
            break
        filtered_boxes.append((x, y, w, h))

    expression = ""
    previous_right = None
    confidences: list[float] = []

    for x, y, w, h in filtered_boxes:
        if previous_right is not None:
            has_operator = "+" in expression or "-" in expression
            parts = expression.replace("-", "+").split("+")
            second_number_started = len(parts) >= 2 and parts[1].isdigit()

            if has_operator and second_number_started and x - previous_right > 20:
                break

        char_img = thresh[y:y + h, x:x + w]
        char_img = cv2.resize(
            char_img,
            None,
            fx=5,
            fy=5,
            interpolation=cv2.INTER_NEAREST,
        )
        padded = cv2.copyMakeBorder(
            char_img,
            20, 20, 20, 20,
            cv2.BORDER_CONSTANT,
            value=0,
        )
        resized = cv2.resize(
            padded,
            (solver.config.image_size, solver.config.image_size),
            interpolation=cv2.INTER_NEAREST,
        )

        prediction = solver.classify_character(resized)
        symbol = prediction.symbol

        if symbol == "plus":
            if expression and expression[-1].isdigit():
                expression += "+"
                confidences.append(prediction.confidence)
        elif symbol == "minus":
            if expression and expression[-1].isdigit():
                expression += "-"
                confidences.append(prediction.confidence)
        elif symbol.isdigit():
            expression += symbol
            confidences.append(prediction.confidence)

        previous_right = x + w

    if not expression:
        return None

    try:
        answer = str(sp.sympify(expression))
    except Exception:
        return {
            "success": False,
            "answer": "",
            "expression": expression,
            "confidence": float(sum(confidences) / len(confidences)) if confidences else 0.0,
            "reason": "invalid expression",
        }

    return {
        "success": True,
        "answer": answer,
        "expression": expression,
        "confidence": float(sum(confidences) / len(confidences)) if confidences else 0.0,
        "reason": "batch heuristics",
    }


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        config = SolverConfig(
            debug=args.debug,
            max_region_candidates=args.max_candidates,
            output_review_dir=resolve_local_path("review_dataset"),
        )
        solver = CaptchaSolver(
            resolve_local_path(args.model),
            resolve_local_path(args.classes),
            config=config,
        )

        if args.warmup:
            payload = {
                "success": True,
                "answer": "",
                "expression": "",
                "confidence": 0.0,
                "reason": "warmup complete",
            }
            print(json.dumps(payload, ensure_ascii=True, separators=(",", ":")))
            return 0

        if not args.image:
            raise ValueError("--image is required unless --warmup is used")

        debug_dir = resolve_local_path(args.debug_dir)

        payload = try_solve_with_batch_heuristics(
            solver,
            args.image,
            debug_dir,
        )

        if payload is not None and bool(payload.get("success")):
            print(json.dumps(payload, ensure_ascii=True, separators=(",", ":")))
            return 0

        result = solver.solve_image(
            args.image,
            debug_dir=debug_dir,
            max_candidates=args.max_candidates,
        )

        if result is None:
            payload = {
                "success": False,
                "answer": "",
                "expression": "",
                "confidence": 0.0,
                "reason": "No candidate found",
            }
        elif not result.parsed.valid:
            payload = {
                "success": False,
                "answer": "",
                "expression": result.parsed.expression or "",
                "confidence": float(result.parsed.average_confidence),
                "reason": result.reason or "Invalid expression",
            }
        else:
            payload = {
                "success": True,
                "answer": result.parsed.answer,
                "expression": result.parsed.expression,
                "confidence": float(result.parsed.average_confidence),
                "reason": result.reason or "Solved",
            }

        print(json.dumps(payload, ensure_ascii=True, separators=(",", ":")))
        return 0
    except Exception as exc:
        payload = {
            "success": False,
            "answer": "",
            "expression": "",
            "confidence": 0.0,
            "reason": str(exc),
        }
        print(json.dumps(payload, ensure_ascii=True, separators=(",", ":")))
        return 1


if __name__ == "__main__":
    sys.exit(main())
