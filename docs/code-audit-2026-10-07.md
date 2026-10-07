# Maintenance audit: 2026-10-07

## Scope and baseline

The working tree was clean at `ee72f5ed061ea27a2efa89a1fea99f2610a8c5b1`.
No user edits were overwritten. Public signatures, the Russian interface,
duplicate comparison stages, deletion confirmation/verification rules, cache
schema 5, and settings JSON property names are preserved.

The solution has six .NET 8 projects. `FindCopy.Core` owns the staged scanner,
platform filesystem interfaces, fingerprint/inventory caches, and guarded
deletion/recovery. `FindCopy.App` is WPF with code-behind and result view models.
The console baseline, acceptance audit, WPF runner, and benchmark harness are
separate projects. `dotnet test` does not execute these console suites.

Before edits, SDK 8.0.425 built all six projects with **0 warnings and 0 errors**.
Linux baseline was **64 passed, 26 Windows skips**; acceptance audit was
**17 passed**. The baseline commit's
[Windows 11 validation](https://github.com/kurasis/FindCopy/actions/runs/37647169085)
also completed successfully.

## Findings and changes

P1 is an operation-blocking error; P2 is a correctness/resource reliability
issue in an exceptional path; P3 is maintenance or diagnostic correctness.

| Priority | File | Cause and bounded correction | Regression evidence |
| --- | --- | --- | --- |
| P1 | `src/FindCopy.App/MainWindow.xaml.cs` | The deletion handler only cleaned up cancellation and success. An unexpected exception left `_cts` set and controls disabled; result projection could also throw before cleanup. A single internal async operation method catches/report errors and always releases state in `finally`. The confirmation and deletion policies are unchanged. | UI18 injects failure and cancellation, checks selection/files and controls, and starts another real search. |
| P2 | `src/FindCopy.Core/Deletion.cs` | An opened keeper was only disposed on a failed boolean check. A throwing metadata provider leaked the handle. Ownership now transfers to the group only after validation; `finally` disposes every rejected keeper. | L1 failed on the original code, then passed. It checks the failed handle is closed, another keeper is used, and both kept copies survive. |
| P2 | `src/FindCopy.Core/ScanCache.cs` | A failing constructor could not be disposed by its caller and left SQLite open. All initialization failures now dispose the connection and rethrow; the existing corruption-only recovery filter remains unchanged. Duplicate initialization statements were consolidated. | L2 failed on the original code, then passed. A malformed schema is preserved; Linux checks native descriptors, Windows checks the file can be renamed immediately. Existing corruption/inventory tests remain enabled. |
| P2 | `src/FindCopy.Core/IoScheduler.cs` | A configuration/notification exception could return control while previously started workers were still using caller-owned state. A `finally` joins started workers before propagation, preserving the original exception. | L3 failed on the original code, then passed. A gated worker stays active when a second domain throws; `Run` must not return until the worker finishes. |
| P2 | `src/FindCopy.App/AppSettings.cs` | In-place JSON writes expose truncated settings on interrupted writes. Settings are now serialized and flushed to an owned temporary file in the same directory, then atomically replaced. Failed replacements preserve the previous file; temporary cleanup and best-effort behavior are retained. Internal path overloads isolate tests from the real user profile. | UI19 round-trips updates, holds an actual Windows reader that denies replacement but permits in-place writes, checks the old JSON survives, verifies temporary cleanup, and saves again after the reader closes. |
| P2 | `.github/workflows/release.yml` | `target_commitish` does not establish the actual target of a pre-existing tag. The guard now checks the exact tag reference and peels annotated tags before accepting the validated commit. | The two mismatched-tag cases failed against the original workflow. Eight Python standard-library tests now cover new/matching/mismatched ordinary and annotated tags, failed validation, PR sources, and the wrong workflow. |
| P3 | `src/FindCopy.Core/IoScheduler.cs` | Wall-clock adjustments could distort autotuning throughput intervals. Monotonic `Stopwatch` timestamps now measure elapsed time; the worker limits and tuning thresholds are unchanged. | Existing NVMe/concurrency tests and L3 exercise the scheduler. A system clock adjustment was not performed. |
| P2 | `build.bat` | Accepting any installed SDK conflicts with `global.json`'s exact pin, and `if errorlevel 1` misses negative .NET host failure codes. SDK resolution, tests, and publishing now check both positive and negative failures. Error branches respect `nopause`; CRLF line endings are preserved. | The first Windows smoke reproduced false success with an unavailable SDK. `tools/test-build-script.ps1` runs the actual batch in owned fixtures with a missing SDK and missing publish project. Both must exit 1 without a pause; SDK failure must stop before publishing. Both Windows CI jobs run it. |
| P3 | `src/FindCopy.Core/Tuning.cs`, `ScanCounters.cs` | Comments described a future cache and system skips absent from issues; both are implemented now. Comments match actual cache/issue behavior. | Existing cache and policy-exclusion regressions. |

## Retained code and dependencies

All three direct packages have concrete runtime uses: Blake3 2.2.1 provides full
hashing; System.IO.Hashing 9.0.9 supplies XXH3 samples and inventory checksums;
Microsoft.Data.Sqlite 8.0.31 provides transactional caches. No dependency was
removed or upgraded. The NuGet advisory check included transitive dependencies
and reported no vulnerable packages using `https://api.nuget.org/v3/index.json`.
This is a point-in-time advisory result.

No confirmed unused production member or tracked temporary/binary output was
found. XAML event handlers/bindings, COM/PInvoke entry points, test reflection,
and public library APIs were considered when checking references. Public
members were retained even if this repository does not call them directly.
Historical CSV/stdout/screenshots under `docs/validation` are evidence, not
temporary files. Python bytecode directories are now ignored.

The scanner's long orchestration method and native recovery code were retained:
their guards cover identity races, cloud hydration, USN checkpoints, and staged
restore intents. Extracting a new framework here would increase review surface
without a demonstrated correctness benefit. Only duplicated cache initialization
and deletion cleanup/unused cancellation assignment were removed.

## Verification

After edits, the Release solution build has **0 warnings and 0 errors**; Linux
baseline is **67 passed, 0 failed, 26 Windows-only skips**; acceptance audit is
**17 passed, 0 failed**. Release source tests are **8 passed**. The three new
resource cases were run against the old code to establish failures first.

SDK build analyzers and nullable/type checking are part of compilation. Targeted
Roslyn style checks use `IDE0005`, `IDE0051`, `IDE0052`, and `IDE0059` with
`--verify-no-changes`. Workflow YAML and embedded Python/Bash syntax are checked.
There is no separately configured repository linter or Python type checker.

Windows runtime validation remains in the existing
[Validate FindCopy workflow](../.github/workflows/validate.yml), using
`windows-11-arm` for native ARM64 and x64 emulation. It executes L1-L3 plus UI18
and UI19 in both theme settings and under a standard account, executes actual
batch failure paths, and verifies the
published executable. The release guards run in its Linux job. Consult each
run's outcome and uploaded stdout for the exact checked source; Linux
cross-compilation does not establish WPF or native Windows behavior.

The cache file change also triggers the existing long-running extended native
workflow. Historical 5/10-million and fully written 50 GiB results are preserved
with their original source attribution; they are not new measurements of this
maintenance change.

## Remaining limits and follow-up

- P3: settings persistence still intentionally hides save errors. Changing its
  return contract or adding a save-error user flow needs a separate decision.
- P3: scheduler callbacks are synchronous; cleanup can wait for in-flight work.
  New timeout/abort semantics would change the public scheduler contract and
  require separate agreement.
- Physical HDD/SATA/NVMe/external-drive profiling, external SMB failures,
  real OneDrive accounts, physical x64 Windows 11, and actual DPI/monitor
  transitions still require the equipment/accounts described in the
  [hardware acceptance plan](hardware-acceptance.md).
- Broad scanner/recovery rewrites, dependency major-version changes, and
  altered deletion/retention rules were intentionally outside this compatible
  maintenance pass.
