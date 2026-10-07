# Implementation Audit

## Current status

The reproduced defects from the [import audit](import-audit.md) are fixed and
covered by passing checks. Additional functionality now includes physical
empty-file groups, exact named-stream verification, bounded large-file
generation, late enumeration fallback, and Windows CI. Full specification
acceptance is still incomplete; see [the roadmap](roadmap.md).

Validation host: Linux x64, .NET SDK 8.0.425. Windows-target compilation and
publication were checked here. Native Windows operations were not executed on this Linux host. The remote
[Windows CI run](https://github.com/kurasis/FindCopy/actions/runs/37594907779)
passed the build and both console suites, including native handle staging.
Interactive WPF behavior and actual cloud/EFS fixtures remain unvalidated.

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
- Cache schema 3 stores creation time. Corruption recovery is restricted to
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
| Release solution build (five projects) | 0 warnings, 0 errors |
| Baseline console suite | 39 passed, 0 failed, 3 platform skips |
| Acceptance console suite | 17 assertions passed, 0 failed |
| Remote Windows CI | Baseline: 42 passed, 0 failed, 0 skipped; acceptance: 17 passed, 0 failed; build, publish, and artifact upload passed |
| Windows self-contained publish from Linux | `FindCopy.exe` generated; not executed |
| Q4/Q5 sparse fixtures larger than 1 GiB | 14 × 64 KiB sampled, no full hashes, unequal files rejected |
| Cancellation with two 101 GiB sparse candidates | Stops after initial full-read blocks; completed quick cache entries retained, no partial full hash |
| Million-file A (`--scale 50`) | 1,000,000 discovered, zero content bytes, zero groups; 2.148 s, sampled 315 MiB working set |
| D generation (`--scale 8 --generate-only`) | Four 2 GiB files; lengths and head/tail reads checked; not a full hashing throughput run |
| Reduced A–D, no cache/cold fill/warm cache | 12 successful runs; warm B/C/D read zero content bytes |

The three Linux baseline skips are native NTFS compression, Windows backend
comparison, and shell Recycle Bin behavior. The Windows baseline includes native
sparse/compression, junction-loop, enumeration, and shell recycling paths. All 42
Windows baseline cases passed without skips, and all 17 acceptance assertions
passed. [Console output](validation/windows-ci.txt) records that run.

CSV evidence: [corrected A–D smoke runs](validation/linux-benchmarks-fixed.csv)
and [million-file run](validation/linux-million-files.csv).
[Import benchmark measurements](validation/linux-benchmarks.csv) remain historical.
Working set is sampled during scanning, not the process lifetime peak including
generation. The 10 ms sampler is approximate; it is not an allocation profiler.
Linux OS-cache timings do not choose production device settings. One million
files does not establish the specified 5–10 million-file memory envelope.

## Remaining acceptance and capability gaps

1. **USN incremental inventory:** journals invalidate cached fingerprints, but
   the scanner still enumerates the full tree. Avoiding unchanged-directory
   enumeration needs a persisted inventory and recovery strategy.
2. **Interactive Windows/cloud evidence:** the Windows build and console suites
   passed in CI, but WPF interaction, actual OneDrive hydration, and EFS fixtures
   still need validation. Cloud policy tests use injected attributes.
3. **Recycle Bin long paths:** the shell backend refuses staged paths of 260
   characters or longer. Scanning and permanent Windows handle deletion support
   extended paths; long-path recycling is a separate missing capability.
4. **Production-scale measurement:** scenarios E–H on HDD, SATA SSD, NVMe, and
   network shares, cold-disk throughput, autotuning, and 5–10 million-file RAM
   acceptance have not been measured. The 101 GiB test checks cancellation,
   not a complete 101 GiB hash.

The full specification must not be marked complete until these gates pass.
