import os

import sympy as sp

from captcha_solver import CaptchaSolver, SolverConfig, is_image_file
from solve_runtime import try_solve_with_batch_heuristics


folder = "test_images"
debug_dir = "debug_output"


def solve_file(solver: CaptchaSolver, path: str) -> tuple[str, str]:
    payload = try_solve_with_batch_heuristics(solver, path, debug_dir)

    if payload is not None and bool(payload.get("success")):
        return str(payload.get("expression", "")), str(payload.get("answer", ""))

    result = solver.solve_image(path, debug_dir=debug_dir)

    if result is None or not result.parsed.valid:
        return "", "Could not solve"

    return result.parsed.expression, result.parsed.answer


def main() -> None:
    os.makedirs(debug_dir, exist_ok=True)

    config = SolverConfig(debug=True, output_review_dir="review_dataset")
    solver = CaptchaSolver("model.keras", "classes.txt", config=config)

    for filename in sorted(os.listdir(folder)):
        if not is_image_file(filename):
            continue

        path = os.path.join(folder, filename)
        expression, result_text = solve_file(solver, path)

        if not result_text:
            try:
                result_text = str(sp.sympify(expression))
            except Exception:
                result_text = "Could not solve"

        print("------------------------------")
        print(f"Filename: {filename}")
        print(f"Equation: {expression} , Results: {result_text}")
        print("------------------------------")
        print()


if __name__ == "__main__":
    main()
