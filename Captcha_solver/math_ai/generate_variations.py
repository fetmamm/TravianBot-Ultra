import argparse
import os
import random

import cv2
import numpy as np

from captcha_solver import IMAGE_EXTENSIONS, read_image
from curate_dataset import LABEL_TOKENS, ensure_review_structure


DATASET_DIR = "dataset"
REVIEW_DIR = "review_dataset"
DEFAULT_VARIATIONS_PER_IMAGE = 3
SEED = 123


def is_image_file(filename: str) -> bool:
    return filename.lower().endswith(IMAGE_EXTENSIONS)


def ensure_parent_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def add_mild_noise(image: np.ndarray, rng: random.Random) -> np.ndarray:
    max_noise = rng.randint(8, 18)
    noise = np.random.randint(0, max_noise, image.shape, dtype=np.uint8)
    return cv2.add(image, noise)


def apply_mild_transform(image: np.ndarray, rng: random.Random) -> np.ndarray:
    rows, cols = image.shape
    transformed = image.copy()

    tx = rng.randint(-4, 4)
    ty = rng.randint(-4, 4)
    scale = rng.uniform(0.9, 1.12)
    shear = rng.uniform(-0.08, 0.08)
    affine = np.float32([
        [scale, shear, tx],
        [shear * 0.35, scale, ty],
    ])
    translation = affine
    transformed = cv2.warpAffine(transformed, translation, (cols, rows), borderValue=0)

    angle = rng.uniform(-9.0, 9.0)
    rotation_scale = rng.uniform(0.94, 1.08)
    rotation = cv2.getRotationMatrix2D((cols // 2, rows // 2), angle, rotation_scale)
    transformed = cv2.warpAffine(transformed, rotation, (cols, rows), borderValue=0)

    if rng.random() < 0.65:
        transformed = cv2.GaussianBlur(transformed, (3, 3), 0)

    morph_roll = rng.random()
    if morph_roll < 0.45:
        kernel = np.ones((2, 2), np.uint8)
        transformed = cv2.dilate(transformed, kernel, iterations=1)
    elif morph_roll < 0.75:
        kernel = np.ones((2, 2), np.uint8)
        transformed = cv2.erode(transformed, kernel, iterations=1)

    alpha = rng.uniform(0.85, 1.18)
    beta = rng.randint(-12, 12)
    transformed = cv2.convertScaleAbs(transformed, alpha=alpha, beta=beta)

    transformed = add_mild_noise(transformed, rng)
    return transformed


def generate_for_class(
    class_name: str,
    dataset_dir: str,
    review_dir: str,
    variations_per_image: int,
    rng: random.Random,
) -> int:
    source_dir = os.path.join(dataset_dir, class_name)
    target_dir = os.path.join(review_dir, class_name)
    ensure_parent_dir(target_dir)

    if not os.path.isdir(source_dir):
        return 0

    created = 0

    for filename in sorted(os.listdir(source_dir)):
        if not is_image_file(filename):
            continue

        image = read_image(os.path.join(source_dir, filename), flags=cv2.IMREAD_GRAYSCALE)
        if image is None:
            continue

        stem, ext = os.path.splitext(filename)

        for index in range(variations_per_image):
            variant = apply_mild_transform(image, rng)
            target_name = f"{stem}_var_{index + 1}{ext}"
            target_path = os.path.join(target_dir, target_name)

            counter = 1
            while os.path.exists(target_path):
                target_name = f"{stem}_var_{index + 1}_{counter}{ext}"
                target_path = os.path.join(target_dir, target_name)
                counter += 1

            cv2.imwrite(target_path, variant)
            created += 1

    return created


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset-dir", default=DATASET_DIR)
    parser.add_argument("--review-dir", default=REVIEW_DIR)
    parser.add_argument("--variations-per-image", type=int, default=DEFAULT_VARIATIONS_PER_IMAGE)
    parser.add_argument("--classes", nargs="+", choices=LABEL_TOKENS)
    args = parser.parse_args()

    ensure_review_structure(args.review_dir)
    rng = random.Random(SEED)
    selected_classes = args.classes or LABEL_TOKENS

    total_created = 0

    for class_name in selected_classes:
        created = generate_for_class(
            class_name,
            args.dataset_dir,
            args.review_dir,
            args.variations_per_image,
            rng,
        )
        print(f"{class_name}: {created}")
        total_created += created

    print(f"\nGenerated total variations: {total_created}")


if __name__ == "__main__":
    main()
