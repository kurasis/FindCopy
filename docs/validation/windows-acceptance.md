# Windows Runtime Acceptance

[CI run 37603823850](https://github.com/kurasis/FindCopy/actions/runs/37603823850)
completed successfully for source `4db11654eabe57e6f239aa3cbb31b0b62c1127cb`
on 2026-10-07. Both Linux and Windows jobs passed. Windows built all six
projects without warnings/errors and published the desktop executable.

| Runner | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Core/baseline, including native filesystem acceptance | 62 | 0 | 0 |
| Audit/race acceptance | 17 | 0 | 0 |
| WPF interaction acceptance | 11 | 0 | 0 |

[Filtered console output](windows-acceptance.txt) preserves the assertion
results. The CI run contains full logs, `FindCopy-windows-x64` executable,
and `FindCopy-ui-validation` screenshots (subject to Actions retention).

## Native filesystem checks

- Real USN journal: warm unchanged root reuses inventory with zero native
  enumeration and zero content reads; a changed file refreshes its parent.
- Real shell recycling from a source path longer than 400 characters, preserving
  the keeper and confirming shell success, not just removal from the source.
- Extended local DOS paths are accepted; extended UNC paths are refused for
  local recycling. Short staging uses the actual file's volume identity.
- Concurrent writer probes remain blocked while the shell recycles the verified
  object and its hard-link alias.
- Actual EFS-encrypted content matches ordinary plaintext; an ACL ReadData
  denial produces ACCESS_DENIED and excludes the candidate.
- Native Cloud Files API registration creates two actual non-local placeholders.
  Default scanning skips both with zero content reads. No provider account or
  network download is involved.

## WPF checks

The runner creates the application's original App and MainWindow components,
runs the dispatcher, and invokes the actual control handlers. Internal startup
and notification hooks isolate the test runner without changing UI behavior.

1. Real scan binds duplicate and empty-file results and restores controls.
2. Select-extras and clear-selection controls retain a keeper.
3. The bound group model refuses selecting every physical copy.
4. Empty-file checkbox controls tab visibility.
5. A staged recovery note remains visible in issues, summary, and warning.
6. Failed deletion preserves membership and selection.
7. Native system-file exclusion qualifies no-duplicate wording.
8. Real scan cancellation restores controls and hides partial results.
9. Sanitized settings validate and the settings window opens/closes.
10. The native cloud consent dialog warns about download/space and declining
    leaves online-only reads disabled.
11. At minimum window width, all selection and deletion controls remain visible.
    The toolbar wraps instead of clipping the delete button.

The [minimum-width screenshot](ui/minimum-width.png) and the other
[UI evidence images](ui/duplicates.png) are preserved in the repository.
Screenshots capture duplicate results, recovery reporting, and policy exclusion.
The PowerShell hardware runner also passed an 18-configuration Windows smoke
with dataset and output paths containing spaces; this is harness validation.
Recovery/failure projection uses injected deletion outcomes; the core native
suite exercises actual handle-bound staging and shell recycling separately.
These automated checks do not certify provider-specific OneDrive service
integration, every Windows build/theme/DPI combination, or physical-device
performance. See [hardware acceptance](../hardware-acceptance.md).

## Published executable and ordinary-user follow-up

[CI run 37611870942](https://github.com/kurasis/FindCopy/actions/runs/37611870942)
passed on source `e1e1aea`, including all baseline, audit, and regular WPF checks
above. A separate credentialed launch verifies a non-administrator Windows
token, repeats the eleven WPF checks, and adds a twelfth check against the
published single-file executable. That child performs actual search, recycling
of an owned duplicate, preservation of its keeper, and exit with code zero.
[Console evidence](windows-standard-user-acceptance.txt) records 12 passed,
0 failed. [Extended acceptance](extended-acceptance.md) explains isolation,
reproduction, dense-file checks, and real five/ten-million-file NTFS evidence.

## Original-path restoration and inventory optimization

[CI run 37616732692](https://github.com/kurasis/FindCopy/actions/runs/37616732692)
passed on source `5623f9f`: Windows baseline/native 74/74 without skips, audit
17/17, regular WPF 12/12, and ordinary-user WPF/published EXE 13/13. Linux passed
59 baseline cases (15 Windows-only skips) and 17 audit cases. Builds have zero
warnings/errors. [Filtered console evidence](windows-recovery-acceptance.txt)
records the added native and desktop assertions.

New native checks verify actual long Unicode-name/ADS recovery with original
file identity, no overwrite on collision, missing directories/junction refusal,
modified/replaced objects, changed `$I` metadata and safe retry, open writers,
and independently recycled hard-link aliases. The WPF history window restores
a real recycled file and disables repeat restoration. The published single-file
EXE performs search, recycling, original-path restoration through its real
history window, refreshed scan guidance, and clean shutdown under a non-admin
account. [Recovery design](../recovery.md) describes the implementation and limits.

[CI run 37617699942](https://github.com/kurasis/FindCopy/actions/runs/37617699942)
passed on source `b6dd8a3`, adding W13 for a shell move interrupted before its
journal update and explicit unsupported/corrupt history handling. Windows is
now 75/75 without skips; Linux is 59 passed with 16 Windows-only skips; audit
is 17/17 on both hosts. Regular WPF remains 12/12 and ordinary-user acceptance
13/13. The filtered evidence and [standard-account output](windows-recovery-standard-user.txt)
record this follow-up. [History screenshot](ui/restored-history.png) shows the
completed restore and disabled repeat action.
