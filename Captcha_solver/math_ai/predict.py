import argparse

from captcha_solver import CaptchaSolver, SolverConfig, read_image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", default="test.png")
    parser.add_argument("--model", default="model.keras")
    parser.add_argument("--classes", default="classes.txt")
    args = parser.parse_args()

    solver = CaptchaSolver(args.model, args.classes, config=SolverConfig(debug=False))

    image = read_image(args.image, flags=0)
    if image is None:
        raise SystemExit(f"Could not read image: {args.image}")

    prediction = solver.classify_character(image)
    print("Prediction:", prediction.symbol)
    print("Confidence:", round(prediction.confidence, 2), "%")


if __name__ == "__main__":
    main()
