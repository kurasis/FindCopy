# Implementation Audit

## Current status

The reproduced import defects and remaining software capability gaps are fixed.
USN directory inventory now avoids unchanged enumeration. Verified long paths
can be recycled through a short same-volume staging namespace, with write
protection and explicit recovery reporting. Windows acceptance includes real
EFS content/access denial and non-local Cloud Files placeholders. The benchmark
runner implements A–H and explicit metadata-memory fixtures.

Linux validation uses .NET SDK 8.0.425 on Debian 13 x64. Native Windows behavior
is validated on GitHub Actions, not inferred from Linux cross-compilation.
Actual HDD/SATA/NVMe/network tuning and provider account integration need target
equipment; see [acceptance follow-up](roadmap.md).

## Corrected audit findings

| Finding | Resulting behavior | Evidence |
| --- | --- | --- |
| R1: exact comparison accepted old-length prefixes | Expected version and live lengths must match before comparison; before/after snapshots must agree | Appending different tails before exact open produces no group; subsequent stable scan also rejects them |
| R2: stale cache results survived mutation | Live version checks guard cache loading, cached sample use, and emitted matches | Mutation after identity capture removes the stale member without content reads |
| R3: unavailable snapshots allowed length-only verification | Unavailable metadata marks the candidate unsupported and excludes it | Both candidates reported unsupported, no content I/O, no match |
| R4: policy skips appeared complete | File/directory exclusions produce reasons and qualify completeness | System skip creates an issue and sets `HasUncheckedFiles` |
| R5: fixed record exceeded target | One active quick hash per record; cached stage hashes remain candidate-only | `Marshal.SizeOf<FileRecord>() == 128` |
| R6: external hard links inflated savings | Estimates require a known complete link set; actual permanent savings require a zero remaining-link count from the open object | Outside link survives, zero freed bytes reported |
| R7: alias replacement deleted an unrelated file | Opened alias identity must equal the verified object's identity | Replacement survives alias deletion attempt |
| R8: generated 2 GiB files overflowed | 64-bit lengths and bounded streaming; no whole-file byte arrays | Four 2,147,483,648-byte files generated with `D --scale 8` |

Additional reviewed paths:

- Recycling stages the verified Windows handle in a private same-volume
  directory before closing it and invoking the shell. The replacement regression
  reuses the original name and confirms that only the staged copy is removed.
  Linux uses an injected staging backend; Windows CI uses the native handle
  rename before an injected shell operation. Actual shell recycling has its
  own Windows baseline test.
- Deletion validates the opened keeper's scanned version and retains its
  handle throughout the group. A keeper modified after scanning cannot
  authorize deletion.
- Required version snapshots cover quick samples, full hashes, ADS hashing,
  and exact comparisons. Exact ADS mode checks stream names, sizes, and bytes,
  including an injected hash-collision fixture.
- Optimized enumeration errors after a partial batch trigger baseline retry
  with complete name deduplication, including hash-collision handling.
- Follow mode refuses directories with unavailable physical identity, so a
  failed identity lookup cannot disable cycle protection.
- Cache schema 5 stores creation time and transactional inventories. Corruption recovery is restricted to
  SQLite corruption/not-a-database errors. Malformed or incomplete USN record
  intervals fail safely to cache invalidation.
- Empty-file results distinguish physical objects and their aliases without
  hashing content. The existing flat empty-file list remains available to the UI.

These are point-in-time checks: scan results do not promise files will remain
unchanged after the scan. Windows destructive operations reopen and verify the
selected objects before acting. The portable deletion backend is for tests
and does not provide Windows mandatory share-mode guarantees.

## Executed validation

| Check | Outcome |
| --- | --- |
| Release solution build (six projects) | 0 warnings, 0 errors |
| Linux baseline | 59 passed, 0 failed, 16 platform skips |
| Linux acceptance regressions | 17 passed, 0 failed |
| Windows core/native baseline | 75 passed, 0 failed, 0 skipped |
| Windows acceptance regressions | 17 passed, 0 failed |
| Windows WPF interaction acceptance | 12 passed, 0 failed, including actual original-path recovery |
| Ordinary-user Windows WPF + published EXE | Non-admin token; 13 passed; actual scan/recycle/restore/keeper preservation and clean exit |
| Windows PowerShell hardware wrapper | 18 configurations passed; paths with spaces |
| Incremental inventory regressions | 18 passing cases included in baseline |
| Million physical sparse A files | 1,000,000 files; 0 content bytes; 0 groups; 2.421 s; 315.37 MiB sampled working set |
| 100,000 physical sparse B files | 6,553,600,000 bytes = 64 KiB/file; 0 groups; 1.748 s; 86.63 MiB |
| B fingerprint-cache warm run | 0 content bytes; 100% hits; 0.957 s; 117.00 MiB |
| Two physical 100 GiB sparse D files | Complete BLAKE3; 214,749,020,160 content bytes; one physical duplicate group; 42.784 s; 48.18 MiB |
| Virtual 5-million metadata records | 0 content bytes/opens; 1.442 s; 1,618.30 MiB |
| Virtual 10-million metadata records | 0 content bytes/opens; 2.088 s; 3,126.54 MiB |
| E–H harness smoke | 18 configurations per scenario, 72 successful runs on unknown virtual storage |
| Two fully written 10 GiB Linux files | Full hashing + exact comparison; 40 GiB logical reads plus samples; cancellation/resume/cache/change passed; peak 59.08 MiB |
| Two fully written 50 GiB Windows files | Full hashing + exact comparison; 200 GiB logical reads plus samples; cancellation/resume/cache/change passed; peak 54.09 MiB |
| Real 5-million-file NTFS tree | Six inventory/change/oracle/cancellation phases passed; peak 1,838.79 MiB; warm native enumeration and content I/O both zero |
| Real 10-million-file NTFS tree | Six phases passed; peak 3,784.91 MiB; warm native enumeration and content I/O both zero |
| Schema-5 real NTFS follow-up | All 18 phases passed; 5/10-million warm observations 1.61/4.23 s; peaks 1,797.93/3,438.91 MiB |
| Schema-5 fully written 50 GiB pair | All five phases passed; full/exact/cancel/resume/cache/mutation; sampled peak 114.96 MiB on separate VM |

Windows runtime/UI evidence and run links are recorded in
[Windows validation](validation/windows-acceptance.md). The baseline includes
real native USN reuse with zero warm directory enumeration/content I/O,
long-path shell recycling, EFS access denial, Cloud Files placeholders,
and blocked writers throughout recycling of all hard-link aliases.
The sixteen Linux skips are Windows-only baseline/native acceptance cases.
Additional dense and real NTFS scale evidence, including reproduction and raw
CSV, is in [extended acceptance](validation/extended-acceptance.md). The original
inventory replay was slower than the hot no-cache oracle. Schema 5 avoids
unchanged blob rewrites and per-entry name allocations; the new 5/10-million
warm observations are 1.61/4.23 s. All phases and variability are preserved
in the report; this is not a guarantee across devices and cache conditions.

The 18 inventory regressions cover unchanged reuse, changed parents,
addition/deletion, aliases, subtree rename, journal reset/gap/unavailability,
incomplete IDs, interrupted enumeration/cancellation, corruption,
recursion switches, competing generations, atomic USN parsing, unchanged-blob
write avoidance, checksummed malformed tails, and allocation-free Unicode replay.
[Inventory design](incremental-inventory.md) explains fallback and transaction rules.
Original-path restoration is implemented in the core and WPF history window;
see [recovery behavior](recovery.md) and [runtime evidence](validation/windows-recovery-acceptance.txt).

CSV evidence: [scale acceptance](validation/linux-scale-acceptance.csv) and
[device harness smoke](validation/linux-device-harness-smoke.csv). Storage
labels are translated to English; numerical data is unchanged. Historical
[correction smoke](validation/linux-benchmarks-fixed.csv),
[million-file run](validation/linux-million-files.csv), and
[import measurements](validation/linux-benchmarks.csv) remain available.

## Reproduction and measurement limits

The scale CSV was produced from source b05db1f3a0d56bf5942e7da6a8035228963b8d4d:

```sh
# Add --label to record the hardware/dataset description in each CSV row.
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /tmp/scale --scenario A --metadata-files 5000000
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /tmp/scale --scenario A --metadata-files 10000000
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /tmp/scale --scenario B --count 100000 --cache
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /tmp/scale --scenario D --size-mib 102400 --copies 2 --sparse
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- /tmp/scale --scenario A --count 1000000
```

The complete D run reads 200 GiB logical bytes plus ten 64 KiB quick samples.
It proves bounded full hashing, rather than just cancellation; sparse holes
and warm OS caches mean the elapsed time is not dense-disk bandwidth.
The virtual fixture feeds compact unique-size metadata and throws on content
access. It measures engine memory, not ten million native file opens or real
path distribution. The newer real NTFS five/ten-million-file dataset verifies
enumeration, inventory reuse, changes, and recovery for its controlled path
and unique-size distribution.

The host reports four .NET processors and a 24,576 MiB GC memory budget. Scan
working set is sampled every 10 ms and excludes dataset generation. Timings
are single-run observations, not percentile statistics or a profiler trace.
The portable backend emits zero Windows-native directory counters; this is
not evidence of avoided Linux enumeration. B warm reuse is fingerprint reuse;
the native Windows USN tests and real NTFS scale fixture demonstrate avoided
directory enumeration.

The E–H smoke uses twelve generated sparse files and an explicitly unknown
storage label. It validates argument handling and all tuning combinations;
actual device acceptance follows [hardware acceptance](hardware-acceptance.md).
No physical-device default was tuned from these cloud timings.
