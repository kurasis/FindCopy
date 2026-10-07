# FindCopy

A Windows 10/11 duplicate-file scanner with a Russian WPF interface and a
separate .NET 8 engine. It compares contents independently of names, extensions,
and timestamps, and checks selected redundant copies again before deletion.
Repository documentation and maintenance instructions are in English.

The reproduced import-audit defects have been corrected. Full specification
acceptance is still pending, particularly native Windows execution, complete
USN incremental enumeration, and device measurements. See the
[current implementation audit](docs/implementation-audit.md) and
[remaining work](docs/roadmap.md). The [original audit](docs/import-audit.md)
records the imported source's failures; it is historical evidence.

## Build and test

Use .NET SDK 8.0.425, or a compatible .NET 8 SDK. The desktop application runs
on Windows. Linux can compile Windows targets and exercise the portable engine,
but cannot validate the WPF interface or native Windows filesystem behavior.

```sh
dotnet restore FindCopy.sln
dotnet build FindCopy.sln -c Release
dotnet run -c Release --no-build --project tests/FindCopy.Tests
dotnet run -c Release --no-build --project tests/FindCopy.Audit
dotnet publish src/FindCopy.App -c Release -o publish
```

Both suites are console runners: `dotnet test` does not execute their assertions.
The current Linux run reports **39 passed, 0 failed, 3 skipped** in the baseline
suite and **17 passed, 0 failed** in acceptance regressions. The solution includes
both suites. GitHub Actions builds and runs them on Linux and Windows, and
publishes the Windows executable. Native CI results must be checked separately.

On Windows, `build.bat test nopause` runs both suites and publishes the
self-contained x64 single file to `publish\FindCopy.exe`.
`build.bat bench nopause` also publishes the benchmark executable.

Package references: Blake3 2.2.1, System.IO.Hashing 9.0.9, and
Microsoft.Data.Sqlite 8.0.31. No application credentials or background services
are required for local validation.

For the Linux cloud workspace, `bash tools/setup-cloud.sh` installs the verified
SDK when needed, restores dependencies, builds the solution, and runs both suites.

## Scanning

- Current-folder and recursive modes, multiple roots, and overlapping-root
  normalization.
- Unicode enumeration and long paths. Optimized Windows batch enumeration
  falls back after errors and suppresses previously emitted entries.
- Size grouping before content reads; physical IDs distinguish copies from
  hard-link aliases. Empty files also have physical duplicate groups, with
  zero savings and no content reads.
- Direct BLAKE3 for small candidates, staged XXH3 samples for larger files:
  start, end, middle from 16 MiB, and quarters from 1 GiB.
- Streaming BLAKE3 and optional exact byte comparison, including named NTFS
  streams when that option is selected. Required metadata is checked around
  reads and again before emitting matches. Missing metadata excludes the
  candidate and produces an issue.
- SQLite fingerprints keyed by identity, size, write/change/creation times,
  and hashing/sampling versions. Cache hits are revalidated against live files.
- USN-based cache invalidation, bounded storage-domain readers, and optional
  NVMe autotuning. USN currently still requires full directory enumeration.
- Policy exclusions, inaccessible files, and changed files qualify result
  completeness and appear in issue counts.

Defaults skip system files, directory reparse links, file symlinks, and non-local
cloud content. Following directory links requires a verified directory identity
for cycle detection. The advanced cloud option prompts about downloads.

## Settings and deletion

Settings: `%LOCALAPPDATA%\FindCopy\settings.json`.
Cache: `%LOCALAPPDATA%\FindCopy\cache.db`, which can be disabled or cleared.
Schema 3 invalidates older cache schemas and includes creation time. Ordinary
scanning works without elevated permissions; USN access may require them.

Selection retains at least one physical copy. Deletion opens that keeper and
the candidate, validates their identities and scanned versions, and compares
bytes again. Each alias's opened identity is checked before deletion.
Permanent deletion uses the verified Windows handle. Physical savings are
reported conservatively when remaining links or allocation are unknown.

Recycle Bin is the default mode. The Windows backend moves the verified open
object into a private same-volume directory before passing its new name to the
shell. Reusing the original name cannot redirect that recycling operation.
Recycling reports zero immediately freed bytes. If the shell operation fails
after staging, the result reports the recovery path under
`.FindCopy-recycle-<id>`; the verified file remains there. The shell backend
requires staged paths shorter than 260 characters and refuses longer ones.
Windows runtime validation remains necessary.

## Benchmarks

```sh
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-bench --scenario all --cache
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-large --scenario D --size-mib 2048 --generate-only
```

```bat
FindCopy.Bench.exe C:\FindCopyBench --scenario B --threshold 256
FindCopy.Bench.exe C:\FindCopyBench --path D:\ExistingDataset --readers 1
```

Options: `--scenario A|B|C|D|all`, `--scale N`, `--size-mib N` (C/D),
`--generate-only`, `--threshold KiB`, `--readers N`, `--buffer KiB`,
`--sample KiB`, `--enum auto|win32`, `--ads`, `--cache`, and `--path <folder>`.
Results append to `<work-dir>/bench-results-v2.csv`, including sampled scan
working set and skipped/error/changed counts.

Generated sizes use 64-bit lengths and bounded streaming buffers. A uses sparse
unique-size files; C/D stream their content instead of allocating whole files.
Generation refuses an existing unrecognized or incomplete dataset rather than
silently reusing it. Large workloads still need sufficient disk space. Keep
generated data outside the repository.

A million-file A run discovered all files with zero content I/O. D with scale 8
successfully generated four 2 GiB files. These Linux checks and reduced A–D
benchmarks are recorded in [validation evidence](docs/implementation-audit.md).
OS-cache measurements do not establish HDD/SSD/NVMe/network tuning.

## Repository layout

- `src/FindCopy.Core`: scanner, filesystem abstraction, hashing, cache, USN,
  scheduling, results, and deletion.
- `src/FindCopy.App`: WPF interface, view models, settings, and resources.
- `tests/FindCopy.Tests`: baseline correctness console suite.
- `tests/FindCopy.Audit`: acceptance and race regressions.
- `tools/FindCopy.Bench`: bounded dataset generator and benchmark harness.
- `build.bat`: Windows test and publishing helper.
- [Specification](docs/specification.md): English digest of all 32 supplied sections.
