# UI audit corrections, 2026-10-10

This follow-up addresses the static Vercel `web-design-guidelines` review and
[the Windows runtime audit](ui-accessibility-audit-2026-10-10.md). The guidelines
URL was fetched successfully again before this review (8,055 bytes). Only the
applicable WPF accessibility, feedback, contrast, and layout guidance is used.
The historical report and its failed probe evidence remain unchanged.

## Corrections

| Priority | Source | Correction and reason |
| --- | --- | --- |
| P1 | `src/FindCopy.App/MainWindow.xaml` | Folder and keeper-rule inputs expose meaningful names. Deletion checkboxes identify the full file path through UI Automation and a tooltip. Selection and deletion rules are unchanged. |
| P1 | `src/FindCopy.App/SettingsWindow.xaml` | All eight numeric inputs expose their visible captions as accessible names, retain label associations, and show supported ranges. Explicit name bindings address the empty names measured through WPF peers with templated labels. |
| P1 | `src/FindCopy.App/MainWindow.xaml`, `MainWindow.xaml.cs` | The options card has a bounded, scrollable viewport that can grow with the window. Compact spacing preserves the result viewport and main actions at 760 x 560 with advanced settings expanded. Options remain available through scrolling. |
| P1 | `src/FindCopy.App/UiPalette.cs`, `App.xaml`, `App.xaml.cs`, window XAML | Dynamic resources follow actual Windows high contrast at startup and after changes; consistent system surface/text pairs cover custom buttons, result Runs, badges, and selection surfaces. Selected file text follows active/inactive native selection colors. The normal palette returns when high contrast is disabled. |
| P2 | `src/FindCopy.App/App.xaml` | Supporting text is darker and now meets the 4.5:1 threshold on the window background. |
| P2 | `src/FindCopy.App/AccessibilityStatus.cs`, main/recovery windows | Phase, completion and recovery text uses polite live regions and emits `LiveRegionChanged` when meaningful text changes. Identical phase text and rapidly changing counters do not repeatedly announce. Actual audible behavior remains a human check. |
| P2 | `src/FindCopy.App/SettingsWindow.xaml.cs` | Invalid numbers and sample sizes exceeding the small-file threshold are rejected inline, focused, and left unchanged. The dialog no longer silently clamps entered values. Loader sanitization remains compatible. |
| P2 | `src/FindCopy.App/AppSettings.cs`, `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs` | Add a reporting atomic-save API while preserving existing `Save` signatures. The current UI accepts settings only after persistence succeeds; failure keeps the modal dialog and entered values for retry and displays the error. |
| P2 | `src/FindCopy.App/SettingsWindow.xaml` | Resizing, a scrollable form, work-area bounds, and a fixed save/cancel area replace the fixed-size dialog. A 420 x 300 regression verifies that its save action remains available. |

No dependencies, installer/update behavior, public API signatures, scanner logic,
or deletion logic were removed or changed. The palette and status helpers have
specific application callers; existing view-model brush properties remain for
compatibility. No inferred-unused code was deleted.

## Validation evidence

- Initial source: `3a1a624308c3be30f810f013bbe99040d4e041bd`.
  [Baseline correctness run 38049272642](https://github.com/kurasis/FindCopy/actions/runs/38049272642)
  passed; [UI audit 38049314874](https://github.com/kurasis/FindCopy/actions/runs/38049314874)
  recorded 21 finding probes per architecture before these changes.
- Corrected application/test source: `40f9e1ee709c259964aafe3a0ff5107ca023893b`.
- [Correctness run 38050759313](https://github.com/kurasis/FindCopy/actions/runs/38050759313):
  all three matrix entries passed. Each Windows architecture passed **101**
  baseline checks (zero skips), **17** acceptance checks, **22** WPF checks in
  each application-theme setting, **4** SMB checks, and **23**
  non-administrator WPF/published-EXE checks. Linux passed **75** baseline
  checks with **26** Windows platform skips and **17** acceptance checks.
- [UI accessibility run 38050759306](https://github.com/kurasis/FindCopy/actions/runs/38050759306):
  both Windows architectures passed **38** probes, with **0** findings,
  **0** execution errors, and **4** explicitly unverified probes.
- Both Windows jobs used `windows-11-arm`, verified Windows 11 client build
  26200 and ARM64 host, and exercised the expected native ARM64 or emulated
  x64 process. SDK 8.0.425 was pinned and checksum verified.
- Local solution compilation passed with **zero warnings and errors**;
  portable baseline/acceptance checks and all **8** Python release-source
  regressions passed. There is no separate lint/type-check command in this
  solution; C# and XAML compilation are the applicable static checks.
- Minimum-size expanded result viewport: **52 DIPs**, previously zero.
  Supporting-text contrast: **5.6973:1**, previously 4.4293:1.
  High-contrast file text: **16.2933:1**, previously 1.0888:1.
  Actual inactive selected file text: **7.8933:1**. The runner did not
  demonstrate active foreground keyboard selection.

Raw reports: [ARM64 JSON](validation/ui/accessibility-fixed-2026-10-10/win-arm64-audit.json)
and [x64 JSON](validation/ui/accessibility-fixed-2026-10-10/win-x64-audit.json).
Screenshots: [minimum-size expanded window](validation/ui/accessibility-fixed-2026-10-10/minimum-size-expanded.png),
[actual Windows high contrast and selected file](validation/ui/accessibility-fixed-2026-10-10/actual-high-contrast.png),
and [scrollable settings](validation/ui/accessibility-fixed-2026-10-10/settings.png).


New WPF regressions exercise every numeric field's invalid ranges and parse
failures, lower/upper boundaries, the sample/threshold constraint, an actual
Windows reader blocking atomic replacement, the modal error state and successful
retry, and a small scrollable settings window. The prior atomic-save regression
now additionally requires a reported failure and a clean successful retry.
Existing accessibility criteria are retained, with additional selected-text and
application-palette restoration checks.

The first correction run exposed eight remaining empty numeric names and a zero
result viewport despite visible action buttons. Those were corrected against
actual Windows evidence rather than changing acceptance thresholds.

## Remaining interactive validation

The hosted desktop still cannot verify audible Narrator output, physical
keyboard interaction without foreground ownership, every visible keyboard focus
indicator, or real 125/150/200% monitor transitions. The audit records these as
unverified, separately from software findings and execution errors. Test these
on an interactive Windows 11 desktop; a running Narrator process or a raster
export is not equivalent evidence. No claim of complete accessibility is made.
