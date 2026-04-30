from captcha_solver import CaptchaSolver, SolverConfig, build_arg_parser, print_candidate_result


def main() -> None:
    parser = build_arg_parser()
    parser.set_defaults(image="full_test.png")
    args = parser.parse_args()

    config = SolverConfig(
        debug=args.debug,
        max_region_candidates=args.max_candidates,
    )
    solver = CaptchaSolver(args.model, args.classes, config=config)

    image_path = args.image or "full_test.png"
    result = solver.solve_image(
        image_path,
        debug_dir=args.debug_dir,
        max_candidates=args.max_candidates,
    )
    print_candidate_result(image_path, result)


if __name__ == "__main__":
    main()
