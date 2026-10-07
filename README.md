# FindCopy

A Windows 10/11 duplicate-file scanner with a Russian WPF interface and a
separate .NET 8 scanning engine. It compares file contents independently of
names, extensions, and timestamps, and supports selection of redundant copies
for deletion after fresh byte comparison.

The supplied source archive has been imported without generated `bin`, `obj`,
or publish outputs. Repository documentation and maintenance instructions are
in English; the original application UI remains in Russian.

**The complete specification is not satisfied yet.** The baseline suite passes,
but the [implementation audit](docs/implementation-audit.md) documents
reproduced correctness and deletion-safety defects. Resolve those defects
before relying on destructive operations. The import/audit does not fix them.

## Requirements and build

Use the .NET 8 SDK. The cloud audit used SDK 8.0.425 on Linux x64.
The desktop application runs on Windows; Linux can compile Windows targets
and run the portable engine tests, but cannot validate the WPF UI or native
Windows behavior.

On Windows:

```bat
build.bat test nopause
```

This runs the baseline suite and publishes the application to
`publish\FindCopy.exe`. `build.bat bench nopause` also publishes the benchmark
executable. The application is published as a self-contained x64 single file.

Direct commands from the repository root:

```sh
dotnet restore FindCopy.sln
dotnet build FindCopy.sln -c Release
dotnet run -c Release --project tests/FindCopy.Tests
dotnet publish src/FindCopy.App -c Release -o publish
```

The original package references are retained: Blake3 2.2.1,
System.IO.Hashing 9.0.9, and Microsoft.Data.Sqlite 8.0.31. No dependency
lockfiles were supplied. No credentials or external application services are
needed for local tests.

## Scanning workflow

- Current-folder-only and recursive scans; multiple roots and overlapping-root
  normalization.
- Win32 Unicode enumeration, extended paths, and an optional batch directory
  backend with physical file IDs.
- Size grouping before content reads and physical-identity grouping for aliases.
- Direct BLAKE3 for small files; staged XXH3 samples for large candidates:
  start, end, middle from 16 MiB, and quarter samples from 1 GiB.
- Streaming BLAKE3, metadata snapshots, and an optional exact byte comparison.
- Optional named NTFS alternate-stream comparison.
- Storage-domain concurrency limits, a global worker budget, and optional NVMe
  autotuning.
- SQLite fingerprint cache and optional USN-based cache invalidation. USN does
  not currently eliminate full directory enumeration.
- Skipped-file/error reporting and structured progress counters.

Defaults skip system files, directory links, file symlinks, and non-local cloud
content. An advanced cloud option prompts about downloads and space usage.
These describe implemented paths; see the audit for correctness limitations.

## Settings and deletion

Performance settings are stored at `%LOCALAPPDATA%\FindCopy\settings.json`.
The default cache is `%LOCALAPPDATA%\FindCopy\cache.db` and can be disabled or
cleared from the UI. USN access may need elevated permissions; ordinary scanning
works without it.

The UI can select redundant copies while retaining at least one physical copy.
Recycle Bin is the default deletion mode; permanent deletion is an explicit
choice. The deletion component performs fresh byte comparison rather than
trusting hashes. Its alias handling and path-based recycling still have safety
limitations documented in the audit. Long paths are supported for scanning,
but the legacy recycling backend rejects paths of 260 characters or longer.

## Validation and audit regressions

```sh
dotnet run -c Release --project tests/FindCopy.Tests
dotnet run -c Release --project tests/FindCopy.Audit
```

At import, the Linux baseline reported **39 passed, 0 failed**. The separate
acceptance regression runner reported **0 passed, 7 failed**, reproducing the
findings in the audit. A failed audit check returns a nonzero exit code; it is
not an expected-failure skip. The metadata-size check is a specification tuning
target, distinguished from the correctness defects. Both suites use temporary
fixtures and clean them up. They are console runners, so use `dotnet run`, not
`dotnet test`, to execute the assertions.

The audit runner is intentionally outside the supplied solution to preserve
its original project membership. Run it explicitly. Native Windows backend
comparison and Recycle Bin tests are conditional in the baseline suite and
were not executed on Linux.

## Benchmarks

```sh
dotnet run -c Release --project tools/FindCopy.Bench -- /tmp/findcopy-bench --scenario all --cache
```

On Windows, use a Windows work directory or the published benchmark executable:

```bat
FindCopy.Bench.exe C:\FindCopyBench --scenario B --threshold 256
FindCopy.Bench.exe C:\FindCopyBench --path D:\ExistingDataset --readers 1
```

Options include `--scenario A|B|C|D|all`, `--scale N`, `--threshold KiB`,
`--readers N`, `--buffer KiB`, `--sample KiB`, `--enum auto|win32`, `--ads`,
`--cache`, and `--path <existing folder>`. Results append to
`<work-dir>/bench-results.csv`.

The default generated workloads are reduced smoke benchmarks, not the full
specification acceptance workloads. The large-file generator currently
allocates an entire file and uses a 32-bit size; scenario D with scale 8
fails. Use existing datasets through `--path` for larger files until the
harness is corrected. Linux OS-cache measurements do not select production
HDD/SSD/NVMe settings.

## Repository layout

- `src/FindCopy.Core`: platform abstraction, scanner, hashing, scheduling,
  cache, USN integration, results, and deletion.
- `src/FindCopy.App`: WPF application, view models, settings, and resources.
- `tests/FindCopy.Tests`: original correctness console suite.
- `tests/FindCopy.Audit`: additional acceptance regressions.
- `tools/FindCopy.Bench`: original benchmark harness.
- `build.bat`: Windows publishing helper.
- [Specification](docs/specification.md): English digest of all 32 supplied sections.
- [Audit](docs/implementation-audit.md): coverage, evidence, limitations, and fixes needed.
