import argparse
import os

import sympy as sp

from captcha_solver import CaptchaSolver, SolverConfig, is_image_file, read_image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--folder", default="input")
    parser.add_argument("--model", default="model.keras")
    parser.add_argument("--classes", default="classes.txt")
    args = parser.parse_args()

    solver = CaptchaSolver(args.model, args.classes, config=SolverConfig(debug=False))

    symbols: list[str] = []
    filenames = sorted(name for name in os.listdir(args.folder) if is_image_file(name))

    for filename in filenames:
        image = read_image(os.path.join(args.folder, filename), flags=0)
        if image is None:
            continue

        prediction = solver.classify_character(image)
        if prediction.symbol == "plus":
            symbols.append("+")
        elif prediction.symbol == "minus":
            symbols.append("-")
        else:
            symbols.append(prediction.symbol)

    expression = "".join(symbols)
    print("Expression:", expression)

    try:
        result = sp.sympify(expression)
        print("Answer:", result)
    except Exception:
        print("Could not solve")


if __name__ == "__main__":
    main()
