import argparse
import os
import re
import shutil
import hashlib

from captcha_solver import IMAGE_EXTENSIONS


DATASET_DIR = "dataset"
REVIEW_DIR = "review_dataset"
UNKNOWN_REVIEW_DIR = "_needs_review"

LABEL_TOKENS = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "plus", "minus"]
SUSPICIOUS_NAME_PARTS = [
    "clipart",
    "download",
    "transparent",
    "png14925",
    "chatgpt image",
]


def is_image_file(filename: str) -> bool:
    return filename.lower().endswith(IMAGE_EXTENSIONS)


def ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def ensure_review_structure(review_dir: str) -> None:
    ensure_dir(review_dir)

    for label in LABEL_TOKENS:
        ensure_dir(os.path.join(review_dir, label))

    ensure_dir(os.path.join(review_dir, UNKNOWN_REVIEW_DIR))


def extract_label_hint(filename: str) -> str | None:
    lower = filename.lower()
    patterns = [
        r"hard__\d+_\d+_(0|1|2|3|4|5|6|7|8|9|plus|minus)_\d+",
        r"test_easy_\d+_\d+_(0|1|2|3|4|5|6|7|8|9|plus|minus)_\d+",
        r"test_easy_\d+_\d+_(plus|minus)_\d+",
        r"test_2_hard_char_\d+_(0|1|2|3|4|5|6|7|8|9|plus|minus)",
    ]

    for pattern in patterns:
        match = re.search(pattern, lower)
        if match:
            return match.group(1)

    stem = os.path.splitext(lower)[0]
    if stem in LABEL_TOKENS:
        return stem

    return None


def is_suspicious_source(filename: str) -> bool:
    lower = filename.lower()
    return any(part in lower for part in SUSPICIOUS_NAME_PARTS)


def move_file(source: str, destination_dir: str) -> str:
    ensure_dir(destination_dir)
    target = os.path.join(destination_dir, os.path.basename(source))

    if os.path.abspath(source) == os.path.abspath(target):
        return target

    base, ext = os.path.splitext(target)
    counter = 1
    while os.path.exists(target):
        target = f"{base}_{counter}{ext}"
        counter += 1

    shutil.move(source, target)
    return target


def audit_dataset(dataset_dir: str) -> tuple[list[tuple[str, str, str]], list[tuple[str, str]], dict[str, int]]:
    mismatches: list[tuple[str, str, str]] = []
    suspicious: list[tuple[str, str]] = []
    counts: dict[str, int] = {}

    for class_name in sorted(os.listdir(dataset_dir)):
        class_dir = os.path.join(dataset_dir, class_name)
        if not os.path.isdir(class_dir):
            continue

        image_count = 0
        for filename in sorted(os.listdir(class_dir)):
            if not is_image_file(filename):
                continue

            image_count += 1
            hint = extract_label_hint(filename)
            if hint and hint != class_name:
                mismatches.append((class_name, filename, hint))

            if is_suspicious_source(filename):
                suspicious.append((class_name, filename))

        counts[class_name] = image_count

    return mismatches, suspicious, counts


def file_hash(path: str) -> str:
    sha = hashlib.sha256()
    with open(path, "rb") as file:
        while True:
            chunk = file.read(8192)
            if not chunk:
                break
            sha.update(chunk)
    return sha.hexdigest()


def curate_to_review(dataset_dir: str, review_dir: str) -> tuple[int, int]:
    ensure_review_structure(review_dir)
    moved_to_review = 0
    moved_to_unknown = 0

    mismatches, suspicious, _ = audit_dataset(dataset_dir)

    handled_sources: set[str] = set()

    for class_name, filename, hint in mismatches:
        source = os.path.join(dataset_dir, class_name, filename)
        if not os.path.exists(source):
            continue

        move_file(source, os.path.join(review_dir, hint))
        handled_sources.add(os.path.abspath(source))
        moved_to_review += 1

    for class_name, filename in suspicious:
        source = os.path.join(dataset_dir, class_name, filename)
        if not os.path.exists(source):
            continue

        if os.path.abspath(source) in handled_sources:
            continue

        hint = extract_label_hint(filename)
        if hint in LABEL_TOKENS:
            move_file(source, os.path.join(review_dir, hint))
            moved_to_review += 1
        else:
            move_file(source, os.path.join(review_dir, UNKNOWN_REVIEW_DIR))
            moved_to_unknown += 1

    return moved_to_review, moved_to_unknown


def move_duplicate_files(dataset_dir: str, review_dir: str) -> int:
    ensure_review_structure(review_dir)
    moved_count = 0

    for class_name in LABEL_TOKENS:
        class_dir = os.path.join(dataset_dir, class_name)
        if not os.path.isdir(class_dir):
            continue

        seen_hashes: dict[str, str] = {}

        for filename in sorted(os.listdir(class_dir)):
            if not is_image_file(filename):
                continue

            source = os.path.join(class_dir, filename)
            digest = file_hash(source)

            if digest not in seen_hashes:
                seen_hashes[digest] = filename
                continue

            move_file(source, os.path.join(review_dir, class_name))
            moved_count += 1

    return moved_count


def import_reviewed_to_dataset(review_dir: str, dataset_dir: str) -> int:
    moved_count = 0

    for label in LABEL_TOKENS:
        source_dir = os.path.join(review_dir, label)
        target_dir = os.path.join(dataset_dir, label)

        ensure_dir(source_dir)
        ensure_dir(target_dir)

        for filename in sorted(os.listdir(source_dir)):
            if not is_image_file(filename):
                continue

            source = os.path.join(source_dir, filename)
            if os.path.exists(source):
                move_file(source, target_dir)
                moved_count += 1

    return moved_count


def clear_reviewed_files(review_dir: str) -> int:
    ensure_review_structure(review_dir)
    deleted_count = 0

    for label in [*LABEL_TOKENS, UNKNOWN_REVIEW_DIR]:
        source_dir = os.path.join(review_dir, label)
        ensure_dir(source_dir)

        for filename in sorted(os.listdir(source_dir)):
            if not is_image_file(filename):
                continue

            file_path = os.path.join(source_dir, filename)
            if not os.path.isfile(file_path):
                continue

            os.remove(file_path)
            deleted_count += 1

    return deleted_count


def print_report(dataset_dir: str, review_dir: str) -> None:
    ensure_review_structure(review_dir)
    mismatches, suspicious, counts = audit_dataset(dataset_dir)

    print("Class counts:")
    for class_name, count in counts.items():
        print(f"  {class_name}: {count}")

    print("\nMismatched label hints:")
    if not mismatches:
        print("  none")
    else:
        for class_name, filename, hint in mismatches:
            print(f"  {class_name} -> review_dataset/{hint}: {filename}")

    print("\nSuspicious source files:")
    if not suspicious:
        print("  none")
    else:
        for class_name, filename in suspicious:
            hint = extract_label_hint(filename)
            if hint in LABEL_TOKENS:
                print(f"  {class_name} -> review_dataset/{hint}: {filename}")
            else:
                print(f"  {class_name} -> review_dataset/{UNKNOWN_REVIEW_DIR}: {filename}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--import-reviewed", action="store_true")
    parser.add_argument("--check-duplicates", action="store_true")
    parser.add_argument("--clear-reviewed", action="store_true")
    parser.add_argument("--dataset-dir", default=DATASET_DIR)
    parser.add_argument("--review-dir", default=REVIEW_DIR)
    args = parser.parse_args()

    print_report(args.dataset_dir, args.review_dir)

    if args.apply:
        moved_to_review, moved_to_unknown = curate_to_review(args.dataset_dir, args.review_dir)
        print(f"\nMoved to review folders: {moved_to_review}")
        print(f"Moved to review unknown: {moved_to_unknown}")

    if args.import_reviewed:
        moved_count = import_reviewed_to_dataset(args.review_dir, args.dataset_dir)
        print(f"\nImported reviewed files to dataset: {moved_count}")

    if args.check_duplicates:
        moved_count = move_duplicate_files(args.dataset_dir, args.review_dir)
        print(f"\nMoved duplicate files to review folders: {moved_count}")

    if args.clear_reviewed:
        deleted_count = clear_reviewed_files(args.review_dir)
        print(f"\nDeleted reviewed files: {deleted_count}")


if __name__ == "__main__":
    main()
