# Working agreements

- Communicate with the user in Russian. Write new repository documentation,
  comments, commit messages, and pull-request descriptions in English.
- Preserve the imported application's Russian UI unless localization changes
  are requested.
- The user authorizes automatic commits, pushes, and merges for requested work.
  Complete relevant validation, report known failures honestly, and avoid
  destructive force pushes or overwriting unrelated remote work.
- Use the existing checkout. Cloud tasks are already isolated; do not create
  Git worktrees unless explicitly requested.
- Keep application fixes separate from source import and audit changes unless
  fixing them is part of the requested task.
- Build with .NET 8. Run the baseline console suite with
  `dotnet run -c Release --project tests/FindCopy.Tests`.
- The additional `tests/FindCopy.Audit` runner contains acceptance regressions
  that currently fail. See `docs/implementation-audit.md`; do not disable or
  weaken them to claim readiness.
- WPF and native Windows filesystem operations require Windows runtime
  validation. A Linux cross-build is compilation evidence only.
