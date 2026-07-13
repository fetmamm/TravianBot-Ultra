Modernize and refactor this codebase.

Requirements:
- Preserve behavior unless I explicitly ask for a functional change.
- Start by identifying dead code, duplicated paths, oversized modules, stale abstractions, and legacy patterns that are slowing changes down.
- For each proposed pass, name the current behavior, the structural improvement, and the validation check that should prove behavior stayed stable.
- Break the work into small reviewable refactor passes such as deleting dead code, simplifying control flow, extracting helpers, or replacing outdated patterns with the repo's current conventions.
- Keep public APIs stable unless a change is required by the refactor.
- Call out any framework migration, dependency upgrade, API change, or architecture move that should be split into a separate migration task.
- If the work is broad, propose the docs, specs, and parity checks we should create before implementation.

Propose a plan to do this.