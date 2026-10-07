# FindCopy

A Windows 10/11 duplicate-file scanner with a Russian WPF interface and a
separate .NET 8 engine. It compares content independently of names, extensions,
and timestamps, and rechecks selected copies before deletion. Repository
documentation and maintenance instructions are in English.

The reproduced import defects and remaining software gaps have been corrected.
Persistent USN inventory, long-path recycling, native EFS/cloud acceptance,
WPF interaction checks, and A–H benchmark workloads are implemented. Actual
HDD/SATA/NVMe/network tuning and provider account integration need target
equipment. See the [implementation audit](docs/implementation-audit.md) and
[acceptance follow-up](docs/roadmap.md). The [import audit](docs/import-audit.md)
is historical evidence.

## Build and test

Use .NET SDK 8.0.425, or a compatible .NET 8 SDK. The desktop application runs
on Windows; Linux can cross-build it and run portable engine checks.

```sh
dotnet restore FindCopy.sln
dotnet build FindCopy.sln -c Release
dotnet run -c Release --no-build --project tests/FindCopy.Tests
dotnet run -c Release --no-build --project tests/FindCopy.Audit
dotnet publish src/FindCopy.App -c Release -o publish
```

On Windows, also run:

```bat
dotnet run -c Release --no-build --project tests/FindCopy.UiTests -- ui-test-artifacts
```

These are console runners; `dotnet test` does not execute their assertions.
Linux reports **54 passed, 0 failed, 8 platform skips**, plus **17 acceptance
regressions passed**. Windows executes the native checks and WPF runner; CI
publishes screenshots and `FindCopy.exe`. See the audit for the verified run.
The solution includes all six projects.

`build.bat test nopause` runs all three suites on Windows and publishes the
self-contained x64 single file to `publish\FindCopy.exe`.
`build.bat bench nopause` also publishes the benchmark executable.
`bash tools/setup-cloud.sh` prepares the Linux cloud workspace and runs both
portable suites. No application credentials or background services are needed.

## Scanning

- Current-folder and recursive modes, multiple roots, overlapping-root
  normalization, Unicode, and long paths.
- Windows batch enumeration with a deduplicated baseline fallback.
- Size grouping before content reads; physical identity separates copies from
  hard-link aliases. Empty physical copies form groups with zero savings.
- Direct BLAKE3 for small candidates; staged XXH3 samples for larger files:
  start/end, middle from 16 MiB, quarters from 1 GiB. Samples only reject files.
- Streaming BLAKE3 and optional exact comparison, including named NTFS streams
  when selected. Metadata is checked around reads and before emitting matches.
- SQLite fingerprints validated against live file identity and version;
  [USN inventory](docs/incremental-inventory.md) reuses unchanged directory
  listings. Changed parents and hard-link aliases are refreshed. Missing
  journals, gaps, resets, incomplete identities, and corrupt listings fall back
  to ordinary enumeration. Following directory links disables inventory reuse.
- Bounded storage-domain readers, optional NVMe autotuning, structured I/O and
  inventory telemetry, and completeness-qualified results.

Defaults skip system files, directory reparse links, file symlinks, and non-local
cloud content. Following directory links requires identity-based cycle detection.
The advanced cloud option asks for consent to downloads and local disk usage.

## Settings and deletion

Settings: `%LOCALAPPDATA%\FindCopy\settings.json`.
Cache: `%LOCALAPPDATA%\FindCopy\cache.db`, which can be disabled or cleared.
Schema 4 invalidates older schemas and stores transactional directory inventories.
Ordinary scans do not require elevation; journal availability varies by account
and filesystem, and unavailable journals never prevent ordinary scanning.

Selection retains at least one physical copy. Deletion opens keeper/candidate,
validates scanned identities and versions, and compares bytes again. Each alias
is checked through its opened handle. Permanent deletion uses the verified
Windows handle; savings are conservative when remaining links are unknown.

Recycle Bin is the default. The verified object is renamed by handle into a
private short directory on the same volume, including for long source paths.
A read guard blocks writes throughout shell recycling. `IFileOperation` must
confirm a recycle item; the operation reports zero immediately freed bytes.
If recycling fails after staging, the UI reports the recovery path and preserves
`original-path.txt`. Successful recycling cleans the staging directory. Windows
Recycle Bin restore metadata refers to the staging location; restoring the
original source path is a manual move, not an application undo feature.

## Benchmarks

```sh
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-bench --scenario all --cache
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-memory --scenario A --metadata-files 10000000
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-large --scenario D --size-mib 102400 --copies 2 --sparse
```

```bat
FindCopy.Bench.exe C:\FindCopyBench --scenario B --acceptance
FindCopy.Bench.exe C:\FindCopyBench --scenario G --path D:\ExistingDataset --label "NVMe model, firmware, filesystem" --os-cache-state warm
```

Use `--help` for options. A–D generate bounded datasets; `--acceptance` chooses
one million A files, 100,000 B files, 1 GiB C files, and 10 GiB D files.
`--count`, `--copies`, and `--size-mib` override sizes. C/D are streamed dense
files unless `--sparse` is explicit. A/B use real sparse files; `--metadata-files`
is an explicitly virtual RAM fixture. `--generate-only` prepares data.

E–H require an existing `--path` and sweep 18 combinations of readers,
thresholds, and buffers. Results append to `bench-results-v3.csv`; OS caches
are never evicted automatically. Keep generated data outside the repository.
Follow [hardware acceptance](docs/hardware-acceptance.md) for device measurements.

Recorded checks: one million physical files with zero content I/O, 100,000 B
files with one 64 KiB sample each, complete BLAKE3 of two 100 GiB sparse files,
5/10 million virtual metadata records, real 5/10 million-file NTFS trees, and
full hashing plus exact comparison of two fully written 50 GiB Windows files.
[Extended acceptance](docs/validation/extended-acceptance.md) records actual
inventory changes, cancellation/recovery, and dense-file results.
Sparse/virtual throughput is not physical disk bandwidth.
[Validation evidence](docs/implementation-audit.md)
records working set, cache state, commands, and limits.

## Repository layout

- `src/FindCopy.Core`: engine, filesystems, cache/inventory, scheduling, deletion.
- `src/FindCopy.App`: WPF interface, view models, settings, and resources.
- `tests/FindCopy.Tests`: baseline, incremental, and native Windows checks.
- `tests/FindCopy.Audit`: acceptance and race regressions.
- `tests/FindCopy.UiTests`: Windows WPF interactions and screenshots.
- `tools/FindCopy.Bench`: bounded generator, RAM fixture, A–H harness.
- [Specification](docs/specification.md): English digest of all 32 supplied sections.
