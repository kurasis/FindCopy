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
- Run acceptance regressions with
  `dotnet run -c Release --project tests/FindCopy.Audit` as well. Both runners
  must pass; do not disable or weaken checks to claim readiness. Consult
  `docs/implementation-audit.md` for remaining specification gaps.
- WPF and native Windows filesystem operations require Windows runtime
  validation. A Linux cross-build is compilation evidence only.
