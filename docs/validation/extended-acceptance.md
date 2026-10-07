# Extended Acceptance

The executable runner adds real dense-file, native NTFS scale, and ordinary-user
published-desktop checks. These are correctness/resource gates on recorded hosts;
no physical HDD/SATA/NVMe/network tuning is inferred from a cloud VM.

## Fully written dense files on Linux

Source `1850d77` uses bounded 1 MiB writes to create two independent regular
10 GiB files. It does not use SetLength, sparse holes, virtual metadata, or
whole-file arrays. The generated pair occupied approximately 20 GiB of workspace
storage. Source and CSV record all reads through the engine counters.

Command:

```sh
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /workspace/findcopy-dense-acceptance --extended-acceptance dense 10240
```

[Raw CSV](linux-dense-acceptance.csv) contains these five passing phases:

| Phase | Result | Logical content I/O | Sampled scan RAM |
| --- | --- | --- | --- |
| Full hash + exact | One EXACT_MATCH group; both files fully hashed and compared | 40 GiB plus ten 64 KiB samples | 55.89 MiB |
| Cancel during full hash | Cancelled after the first full-read block; no completed full hash | 1 MiB full read plus samples | 58.65 MiB |
| Resume after cancellation | Both complete hashes recomputed; one group | 20 GiB | 59.04 MiB |
| Fingerprint cache warm | One group with no content reads | 0 | 59.07 MiB |
| Change four bytes at midpoint | No group; cached candidate invalidated; rejected before full hash | 192 KiB | 59.08 MiB |

Generation precedes the timer and can warm OS caches. The host is Debian 13 x64,
with four .NET processors and a 24,576 MiB GC budget. Working set is sampled
at 10 ms intervals, not an allocation profiler. Portable native-directory
counters are zero because that backend does not expose Windows telemetry.
The runner removes only its own completed dense dataset, preserving the CSV.

## Fully written dense files on Windows

The independent dense job in [run 37608454950](https://github.com/kurasis/FindCopy/actions/runs/37608454950)
passed on source `738e049`. The host is Windows Server 2025, with four processors,
16,378 MiB physical memory, and a temporary NTFS volume. Each of the two regular
files was fully written to 50 GiB; the runner rejects sparse or compressed
attributes. [Raw CSV](windows-dense-acceptance.csv) records:

| Phase | Result | Logical content I/O | Time | Sampled scan RAM |
| --- | --- | --- | --- | --- |
| Full hash + exact | One EXACT_MATCH group | 200 GiB plus ten 64 KiB samples | 544.74 s | 39.84 MiB |
| Cancel during full hash | Cancelled after first full-read block | 1 MiB plus samples | 0.30 s | 48.34 MiB |
| Resume | Both full hashes recomputed | 100 GiB | 263.89 s | 54.09 MiB |
| Warm fingerprint cache | One group, no content reads | 0 | 0.19 s | 32.96 MiB |
| Four-byte midpoint change | No group, no full hash | 192 KiB | 0.02 s | 33.14 MiB |

The largest observed dense scan working set was 54.09 MiB. Generation precedes
measurement; OS caches are not evicted. The CSV reports logical engine reads,
not physical storage traffic or cold-device throughput.

## Real NTFS scale and reproduction

The native job in [run 37608454950](https://github.com/kurasis/FindCopy/actions/runs/37608454950)
passed all six phases at 20,000, 5,000,000, and 10,000,000 real files, on source
`738e049`. [Raw CSV](windows-native-scale-acceptance.csv) preserves all 18 rows.
The host has the same Windows Server 2025/4-processor/16,378 MiB configuration
as the dense job. The temporary NTFS volume has its USN journal enabled.

| Phase | 5-million result | 10-million result |
| --- | --- | --- |
| Inventory fill | 503 native directories; 10.83 s; 1,767.00 MiB | 1,003 native directories; 28.75 s; 3,784.91 MiB |
| Unchanged inventory | 0 native directories; 0 content bytes; 12.35 s | 0 native directories; 0 content bytes; 24.96 s |
| Rename, add, delete | 5,000,002 files; 3 native directories; one duplicate group | 10,000,002 files; 3 native directories; one duplicate group |
| Fresh no-cache oracle | Same group paths, hash, and totals; 2.09 s | Same group paths, hash, and totals; 7.93 s |
| Cancel inventory replay | Interrupted at 40,000 discovered files | Interrupted at 10,000 discovered files |
| Recovery after cancel | Same complete groups; 0 native enumeration/content reads | Same complete groups; 0 native enumeration/content reads |

Largest sampled scan working sets across the six phases were 1,838.79 MiB
(1.80 GiB) and 3,784.91 MiB (3.70 GiB), respectively. These are process working
sets sampled every 10 ms, including retained runtime/cache state, not just
the fixed record array. Dataset generation is outside the scan timer.

The tree uses short fixed-format filenames, 10,000 files per leaf directory,
Unicode paths in one fifth of the leaf buckets, and unique EOF lengths.
Sparse content occupies little disk space; actual NTFS entries and metadata
are real. This proves native enumeration and inventory correctness at these
counts, with this controlled path/size distribution. Representative customer
trees can have longer paths, many same-size candidates, and much larger results.

On this host warm inventory replay is slower than the fresh no-cache oracle
against the OS-cached tree (12.35 vs 2.09 s; 24.96 vs 7.93 s). Avoided native
enumeration is verified; a universal elapsed-time improvement is not.
Inventory serialization/cache overhead remains an optimization opportunity.
No physical-device default is changed from these observations.

### Schema 5 optimization follow-up

The native job in [run 37615247969](https://github.com/kurasis/FindCopy/actions/runs/37615247969/job/112771898985)
passed all 18 phases on source `88762bc`. [Optimized raw CSV](windows-native-scale-optimized.csv)
uses the same controlled dataset and a separate VM reporting the same Server
2025/four-processor/16,378 MiB configuration. Schema 5 retains unchanged
directory blobs, advances only the atomic root checkpoint, and decodes UTF-16
name spans without allocating strings in validation/replay.

| Phase | Five million | Ten million |
| --- | --- | --- |
| Inventory fill | 11.97 s; 1,727.05 MiB; 503 native directories | 58.74 s; 3,438.91 MiB; 1,003 native directories |
| Warm inventory | 1.61 s; 1,797.93 MiB; 0 native directories/content reads | 4.23 s; 3,429.04 MiB; 0 native directories/content reads |
| Rename/add/delete | 1.61 s; only 3 native directories; same group as fresh | 3.03 s; only 3 native directories; same group as fresh |
| Fresh no-cache oracle | 2.31 s | 9.90 s |
| Cancel replay | Interrupted at 16,385 files; 0.097 s | Interrupted at 10,000 files; 0.061 s |
| Warm recovery after cancel | 1.31 s; 0 native enumeration/content reads | 19.50 s; 0 native enumeration/content reads |

Peak sampled working sets across all phases are 1,797.93 MiB (1.76 GiB) and
3,438.91 MiB (3.36 GiB). Warm elapsed observations changed from 12.35 to 1.61 s
and from 24.96 to 4.23 s, approximately 7.7x and 5.9x. These are single-run
observations on separate cloud VMs, not matched-device percentile statistics.
Initial ten-million inventory fill was slower (58.74 vs 28.75 s), and post-cancel
recovery took 19.50 s despite zero content I/O/native enumeration. Retaining
these values makes the timing variability visible; steady latency across
memory/storage/cache conditions is not established by this suite.

Correctness gates still require complete membership, zero warm native
enumeration/content reads, fresh-oracle agreement, affected-parent refresh,
and complete recovery after cancellation. Baseline I16 uses SQLite triggers
to forbid any unchanged-blob insert/update/delete while confirming that the
checkpoint advances; I17/I18 cover malformed tails and allocation-free Unicode.

The same dense mode accepts up to 102400 MiB per file. The dedicated workflow
runs two fully written 50 GiB files on a temporary NTFS runner volume. Dense and
native checks use independent jobs so neither dataset competes for disk space.

Native mode creates actual sparse NTFS files with unique real EOF lengths.
It uses Unicode directory names and independent entries, not an injected
filesystem. The suite checks full inventory fill, unchanged zero-enumeration
reuse, rename/add/delete changes, a newly introduced independent duplicate pair,
a fresh full-scan oracle, cancellation during inventory replay, and warm recovery.
Successful runs restore the generated baseline so a larger count can extend it.

```powershell
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- D:\NativeAcceptance --extended-acceptance native 5000000
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- D:\NativeAcceptance --extended-acceptance native 10000000
```

Native acceptance requires a readable USN journal. The isolated CI fixture
explicitly enables journaling on its temporary volume; ordinary application
scanning still works without a journal or elevation. These commands create
millions of files and require substantial time and metadata disk space.

## Published executable under an ordinary account

[CI run 37611870942](https://github.com/kurasis/FindCopy/actions/runs/37611870942)
passed on source `e1e1aea`: Windows core 62/62 without skips, audit 17/17,
regular WPF 11/11, and standard-account WPF/published-desktop 12/12.
Linux also passed 54 baseline cases (8 Windows-only skips) and 17 audit cases.
[Standard-account console output](windows-standard-user-acceptance.txt) records
`administrator=false` and the actual published-executable acceptance assertion.
The run provides the published `FindCopy-windows-x64` artifact and ordinary-user
screenshots/logs in `FindCopy-standard-user-validation` (subject to retention).

`tools/test-standard-user.ps1` creates a temporary local account in Users,
launches the self-contained WPF runner with its profile loaded, verifies that
the token is not an administrator, and removes the test account afterward.
Only read access to the checkout and write access to its artifact directory
are granted. Test TEMP is explicitly assigned to the account's writable area
because credentialed launches otherwise inherit the administrator's TEMP.
The randomized account password is never printed or persisted in the repository.

The runner exercises the existing eleven WPF checks and uses UI Automation
against the separately published single-file `FindCopy.exe`: launch, search,
select an extra copy, confirm actual recycling, retain one file, and close.
This checks the real application's startup/resources and destructive-action
flow, beyond constructing an in-process MainWindow. The automation acts only
on owned generated fixtures and dialogs belonging to the child process.
The published child must exit with code zero after closing its main window.
The fixture verifies one source copy remains and reports one clean removal;
native shell recycling semantics also have independent baseline coverage.
