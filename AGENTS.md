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
- When developing or changing a web interface, use
  [web-design-guidelines](.agents/skills/web-design-guidelines/SKILL.md) to review
  the result. For UI, UX, or accessibility audit requests, explicitly apply this
  skill and state that it is being used. Fetch its current guidelines before
  each review; if fetching fails, report the limitation. Apply web-specific
  rules only to web content; for the existing WPF interface, distinguish
  applicable general guidance from HTML/browser requirements.
- Build with .NET 8. Run the baseline console suite with
  `dotnet run -c Release --project tests/FindCopy.Tests`.
- Run acceptance regressions with
  `dotnet run -c Release --project tests/FindCopy.Audit` as well. Both runners
  must pass; do not disable or weaken checks to claim readiness. Consult
  `docs/implementation-audit.md` for remaining specification gaps.
- WPF and native Windows filesystem operations require Windows runtime
  validation. A Linux cross-build is compilation evidence only.
- Windows CI must use `windows-11-arm`. Require a Windows 11 client OS and
  ARM64 host; do not fall back to Windows Server. Validate both native
  `win-arm64` and self-contained `win-x64` processes under Windows emulation.
  Use the pinned SDK in `global.json` and the verified Windows SDK installer.
- On Windows, also run `dotnet run -c Release --project tests/FindCopy.UiTests`.
  The WPF runner emits screenshots and exercises real scanning, controls,
  cancellation, cloud consent, settings, and recovery reporting.
- Distinguish virtual metadata, sparse logical reads, physical disk throughput,
  and OS cache state. Use `docs/hardware-acceptance.md` for device measurements.
