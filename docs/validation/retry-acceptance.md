# Inventory Repair and Interrupted Restore Acceptance

Source `bbede45486541ad41dae5362498100fda7ee4554` passed
[CI run 37621870967](https://github.com/kurasis/FindCopy/actions/runs/37621870967).
Windows core/native: 82 passed, zero failures/skips; audit: 17 passed;
regular WPF: 13 passed. The non-admin account ran 14 checks, including actual
search, recycling, original-path restoration, keeper preservation, and clean
exit through the published single-file EXE. Linux ran 61 core cases and 17
audit cases, with 21 Windows-only skips. The six-project Release build had
zero warnings/errors. These Windows runs use the cloud Server runner, not a
Windows 10/11 client acceptance claim.

[Filtered native output](windows-retry-acceptance.txt) and
[standard-account output](windows-retry-standard-user.txt) preserve the checks.

I19 first reproduced a failed atomic inventory repair before the fix. Its five
valid-checksum corruptions affect the second entry's name, directory flag,
identity flag, size, or timestamp. Each must refresh exactly one directory,
emit each actual file once, match a fresh scan, and advance the checkpoint.
The following warm scan reuses all three repaired directory listings with
zero enumeration/content reads. Semantic format exceptions now return an
invalid-listing result instead of making the root uncommittable.

R11 proves the optional restore-intent field survives a journal restart and
older JSON without the field does not gain intent. Native W14–W18 exercise:

- A returned staged object with an unrelated cleanup blocker. The blocker
  survives, the operation reports returned data with pending cleanup, and a
  later retry finishes after the fixture removes its own blocker.
- A returned Recycle Bin object before and after its verified `$I` was removed.
  Both interrupted states reconcile without renaming or copying the data again.
- A same-size/date replacement at the original path and changed bin metadata.
  Both are refused; the pending history and metadata survive for a safe retry.
- Intent recorded before an unperformed rename, with a destination conflict.
  The destination survives; removing it permits the pending operation to resume.
- A missing source and matching original object without restore intent.
  Recovery refuses adoption and leaves the bin metadata/history untouched.

WPF UI14 uses an actual staged object returned to its original path. The history
shows **Завершить восстановление**, allows retry, completes cleanup through the
button, and then disables repeated recovery. It runs under both regular and
non-admin tokens. This in-process pending-cleanup fixture is distinct from the
published-EXE test, which exercises its complete scan/recycle/restore workflow.

The service flushes intent before handle-bound no-replace renaming and marks
the entry completed after cleanup. Reconciliation requires the original
volume/file identity, creation time, size, and last-write time; source absence
alone is insufficient. Existing bin metadata remains digest-verified and
locked. Permission failures and changed objects/metadata require investigation
instead of unsafe cleanup. See [recovery behavior](../recovery.md).

[Layout follow-up CI 37622466999](https://github.com/kurasis/FindCopy/actions/runs/37622466999)
passed source `a49d4a4` with the same counts. UI14 additionally checks path/status
column widths after layout and at the window's minimum width. The path remains
ellipsized with its full tooltip, and the pending status remains readable.
[Non-admin minimum-width screenshot](ui/pending-history.png) shows the result.
