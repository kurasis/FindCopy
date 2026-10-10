# Windows UI accessibility audit, 2026-10-10

This is a runtime follow-up to the review using the vendored Vercel
`web-design-guidelines` skill. Its current rules URL returned HTTP 200 on the
audit date. HTML/browser-only rules are excluded from this WPF review.
Application source and behavior have not been changed in this task.

## Run provenance and outcomes

- Audited source: `1b988114e9238dcd6821d91c8e760af2c035bd3c`.
- [Windows UI accessibility run 38048771989](https://github.com/kurasis/FindCopy/actions/runs/38048771989).
- [Existing correctness run 38048771988](https://github.com/kurasis/FindCopy/actions/runs/38048771988).
- Both audit jobs ran on `windows-11-arm`, with verified Windows 11 client
  build 26200, ARM64 host, and the expected native ARM64 or emulated x64 process.
  The numeric runtime OS string `Windows 10.0.26200` is the kernel version,
  rather than evidence of Windows 10 or Windows Server.
- SDK 8.0.425 was pinned and its Windows archive checksum verified.

| Architecture | Passed probes | Finding probes | Audit execution errors | Unverified probes |
| --- | ---: | ---: | ---: | ---: |
| Native ARM64 | 13 | 21 | 0 | 4 |
| Emulated x64 | 13 | 21 | 0 | 4 |

Both architectures produced identical probe outcomes. A finding count counts
individual affected controls and overlapping checks, rather than 21 distinct
bugs. The audit intentionally returns a failing exit code when findings remain;
neither workflow errors nor existing UI defects are suppressed to claim success.
Raw reports are retained as [ARM64 JSON](validation/ui/accessibility-2026-10-10/win-arm64-audit.json)
and [x64 JSON](validation/ui/accessibility-2026-10-10/win-x64-audit.json).

The separate existing correctness run completed successfully on all three
matrix entries. Each Windows architecture passed 101 baseline checks with zero
skips, 17 acceptance checks, 19 WPF checks in each application-theme setting,
4 SMB checks, and 20 non-administrator WPF/published-EXE checks. Linux passed
75 baseline checks with 26 Windows platform skips and 17 acceptance checks.
These successful functional regressions do not invalidate the accessibility
findings from the separate audit.

## Confirmed findings

| Priority | Source | Runtime evidence | Minimal correction |
| --- | --- | --- | --- |
| P1 | `src/FindCopy.App/MainWindow.xaml:53` | Both deletion checkboxes expose an empty UI Automation name; the generic tooltip is only help text. | Give each checkbox a unique accessible name identifying its file and directory. |
| P1 | `src/FindCopy.App/MainWindow.xaml:104`, `:166`; `SettingsWindow.xaml:34` and the seven other numerical inputs | Folder, keeper-rule, and all eight performance inputs expose empty AutomationPeer names. | Associate visible labels with the inputs using `Label.Target` and `AutomationProperties.LabeledBy`, or add meaningful names. |
| P1 | `src/FindCopy.App/MainWindow.xaml:70`, `:117`, `:182`, `:220` | At the declared minimum 760 x 560 size with advanced settings expanded, the result viewport has zero height. Delete starts at y=618; recovery/export start at y=562, beyond the window. | Make advanced settings scrollable or constrain their height while preserving usable results and fixed action areas. |
| P1 | `src/FindCopy.App/App.xaml:8`; `MainWindow.xaml:57`, `:59`, `:182` | Windows high contrast is actually enabled, with system window `#202020` and text `#FFFFFF`. The file text remains `#111827` on the native tree's `#202020`: only **1.0888:1** contrast. | Use consistent system foreground/background brushes in high contrast, including text Runs, custom templates, selection areas, and result surfaces. |
| P2 | `src/FindCopy.App/App.xaml:12`; `MainWindow.xaml:84` | Supporting text `#6B7280` on `#F3F5F9` measures **4.4293:1**, below 4.5:1 for normal small text. | Darken supporting text or adjust the background to meet the contrast threshold. |
| P2 | `src/FindCopy.App/MainWindow.xaml:145`; `RecoveryWindow.xaml:31` | Both status elements have `AutomationProperties.LiveSetting=Off`; no explicit announcement mechanism is present. | Use polite live regions and raise appropriate UI Automation events for phase/completion/recovery changes, without announcing rapidly changing counters continuously. |

Screenshots preserve the [minimum-size clipping](validation/ui/accessibility-2026-10-10/minimum-size-expanded.png)
and [actual high-contrast rendering](validation/ui/accessibility-2026-10-10/actual-high-contrast.png).
The latter also shows native controls switching palette while application text
and custom backgrounds retain their light colors. This is an observed failure,
rather than a recommendation to add an optional dark theme.

## What was positively verified

- The five main action buttons expose meaningful Russian accessible names.
- An actual bounded duplicate scan realizes file selection checkboxes; a zero
  checkbox count cannot silently pass the accessibility check.
- WPF forward focus traversal from the folder field reaches the browse button.
  Its inherited `FocusVisualStyle` is present; absence of a custom focus trigger
  alone therefore does not prove missing focus visuals.
- `SPI_SETHIGHCONTRAST` enabled the real Windows setting, WPF observed the
  change, and the original disabled state was restored in `finally` on both
  runners.
- Narrator launched through Windows Shell from the trusted system path and
  remained running for the startup smoke check. The started process was stopped.
  No `runas` verb or application elevation requirement was introduced.
- Both `GetDpiForWindow` and WPF reported actual **96 DPI**, with one monitor.

## Checks that remain unverified

1. **Narrator speech and user experience.** A running Narrator process does not
   prove that names, state changes, confirmations, or errors are spoken correctly.
   The hosted runner has no verified listening session. Complete a human screen
   reader review after fixing labels and status notifications.
2. **Physical keyboard input.** The window obtains WPF focus but does not own
   the hosted desktop foreground. The probe therefore does not send global keys
   to an unowned foreground window or blame the application for that limitation.
3. **Complete keyboard navigation and visible focus.** Programmatic WPF focus
   traversal is not physical Tab/Space input. Inspect every result action, context
   menu, dialog, cancellation path, and focus indicator on an interactive desktop.
4. **Real DPI changes and monitor transitions.** This run verifies 96 DPI only.
   Check 125/150/200% scaling and transitions on a real multi-monitor Windows 11
   desktop. Raster exports, injected DPI messages, registry edits requiring logoff,
   and unsupported display overrides are not equivalent evidence.

The prior static observations about silently sanitized out-of-range settings
and hidden settings-save failures have not been dynamically exercised by this
accessibility run. They remain separate follow-up work.

## Reproduce

```powershell
dotnet build tests/FindCopy.UiTests -c Release -m:2 -p:RuntimeIdentifier=win-arm64 -p:SelfContained=true
./tests/FindCopy.UiTests/bin/Release/net8.0-windows10.0.17763.0/win-arm64/FindCopy.UiTests.exe ui-accessibility-artifacts --accessibility-audit
```

Use `win-x64` for emulation. The separate **Audit Windows UI accessibility**
workflow provides the same checks and always uploads reports/screenshots. The
default correctness runner remains separate and retains its existing assertions.
Run only in an isolated Windows session: the audit temporarily changes the
session's high-contrast setting and launches Narrator, with cleanup afterward.
