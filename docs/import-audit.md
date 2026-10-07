# Import Audit (Historical)

This records the unmodified imported algorithms at commit
`9eb48b2508e668dcd3846f2c7297388d35fca723`. Its failures and source line links
describe that revision. Consult the [current audit](implementation-audit.md)
for corrected behavior and remaining work.

## Verdict and scope

**The full specification is not implemented correctly yet.** Most architectural
components exist, including the later cache and USN layers, but acceptance
regressions reveal correctness and deletion-safety defects. The original
baseline tests passing does not resolve these defects.

The audit reviewed all supplied source files and the 32-section specification.
Sources came from `FindCopy - Copy.zip`; generated `bin`, `obj`, and publish
artifacts were excluded. Application source and original test logic were kept
unchanged. README/build-helper text was translated to English. This audit adds
a separate regression runner rather than changing the algorithms.

Validation host: Linux x64, .NET SDK 8.0.425. The SDK archive's SHA-512 was
checked against Microsoft's release metadata before use. Native Windows runtime
behavior was not executed. Build artifacts from the upload were not trusted as
validation evidence.

## Executed checks

| Check | Outcome |
| --- | --- |
| `dotnet run -c Release --project tests/FindCopy.Tests` | 39 passed, 0 failed, exit 0 |
| `dotnet build FindCopy.sln -c Release -m:2` | Four original projects built, 0 warnings/errors |
| `dotnet publish src/FindCopy.App -c Release --no-restore -o /tmp/findcopy-publish -m:2` | Windows self-contained output generated; not run |
| `dotnet run -c Release --project tests/FindCopy.Audit` | 0 passed, 7 failed, exit 1; R1–R7 reproduced |
| Benchmark A/B/C/D, default scale, no cache/cold fill/warm cache | 12 runs completed; expected candidate groups and read counts observed |
| Benchmark B, thresholds 256 KiB and 4 MiB, in addition to default 1 MiB | Both completed; threshold impact observed, no hardware tuning claim |
| Benchmark D, `--scale 8` | Failed with `OverflowException` at generator line 175, exit 134 |

The baseline is a console runner: `dotnet test` alone does not execute its
assertions. Conditional Windows backend-comparison and Recycle Bin tests were
not run on Linux. No skip message was emitted for a test body in the 39 executed
cases, but platform-conditional cases omitted from the run remain unexecuted.
The baseline runner counts an early-return skip inside a test as a pass; its
counts must be interpreted with its output and platform conditions.

## Reproduced findings

### R7 — High: alias replacement can delete an unrelated file

Location: [Deletion.cs, alias loop](../src/FindCopy.Core/Deletion.cs#L215).

The alias identity is checked by path before opening the alias for deletion.
The opened handle's identity is not checked against the verified physical
object. Replacing the alias after `GetIdentity` returns and before
`OpenCandidate` causes the replacement to be deleted without byte verification.

The regression creates a duplicate with an in-tree hard link, uses an
`IFileSystem` wrapper to replace the alias immediately after identity lookup,
and invokes permanent deletion on the selected copy. Observed:
`replaced=True`, `unrelatedReplacementSurvives=False`, `outcomeDeleted=True`.
This is an executed core/platform-contract defect on Linux. The same missing
handle-identity validation exists in the Windows call path, but native Windows
execution has not been performed.

Required correction: validate the identity of the opened alias handle and
bind verification/deletion to that object. A path-only precheck is insufficient.

### R1 — High: exact verification accepts unequal appended tails

Location: [ScanController.cs, CompareFiles](../src/FindCopy.Core/ScanController.cs#L944).

The comparison uses the size discovered before hashing. Initial comparison
snapshots are not checked against that size or the hashed file version. If both
files grow before exact comparison, the loop compares only the old prefix;
unchanged before/after comparison snapshots do not reject the extra tails.

The regression appends byte `1` to one file and byte `2` to the other between
hashing and the first exact open. Observed: one `ExactMatch` group,
`currentBytesEqual=False`, and zero changed-file reports.

Required correction: check current lengths and relevant identities/metadata
against the expected version before comparison, validate after comparison, and
never claim complete byte equality from an old-length prefix.

### R2 — High: cached matches are returned after a candidate changes

Locations: [LoadFromCache](../src/FindCopy.Core/ScanController.cs#L681) and
[FullHashStage](../src/FindCopy.Core/ScanController.cs#L541).

Cache validation uses metadata already stored in the record. A full cache hit
skips file reading and there is no later metadata revalidation before emitting
the group. A change after identity/enumeration metadata collection can therefore
leave an obsolete hash in the result.

The regression first fills the cache, then mutates one file immediately after
its identity metadata is captured in the next scan. Observed: one duplicate
group, two cache hits, zero content reads, unequal current bytes, and zero
changed-file reports. USN is deliberately disabled, a supported configuration.

Required correction: revalidate cached candidates at a meaningful use/result
boundary and invalidate changed versions. Cache/USN must remain correctness
independent, with a documented concurrent-change policy.

### R3 — High: unavailable snapshots weaken change detection to size only

Location: [HashOnce](../src/FindCopy.Core/ScanController.cs#L598).

When `TryGetSnapshot` fails, only length is checked after hashing. A same-size
modification after a block has been consumed is not rejected; the old digest
can still enter a duplicate group.

The regression makes snapshot acquisition unavailable and modifies a 4 KiB
candidate after its content block is hashed. Observed: one duplicate group,
unequal current bytes, and zero changed-file reports.

Required correction: use a supported metadata fallback or conservatively mark
verification unavailable/exclude the file. Do not represent a length-only check
as the specified modification guarantee.

### R4 — Medium: policy skips do not qualify completeness

Locations: [HasUncheckedFiles](../src/FindCopy.Core/Results.cs#L60) and
[system/hidden skip handling](../src/FindCopy.Core/ScanController.cs#L251).

System/hidden skips increment `SkippedFiles` without adding issues.
`HasUncheckedFiles` ignores that counter. Observed: `skipped=1`, `issues=0`,
`hasUnchecked=False`. The UI uses that flag to choose its no-duplicates wording,
so exclusions can be presented as an unqualified complete result.

Required correction: account for policy exclusions in the completeness flag
and expose exclusion reasons consistently.

### R5 — Optimization target missed: 152-byte fixed record

Location: [FileRecord](../src/FindCopy.Core/FileRecord.cs#L10).

`Marshal.SizeOf<FileRecord>()` is 152 on the tested host, compared with the
specification's approximate <=128-byte target. This is a memory-design target,
not evidence of a false duplicate. Array doubling, sorting/index arrays,
candidate groups, cached hashes, and result objects add further memory. At ten
million records, fixed live records alone represent about 1.42 GiB before
capacity slack and paths. Full-scale memory acceptance remains unmeasured.

### R6 — Medium: an outside hard link invalidates reported savings

Location: [Deletion.cs, FreedBytes](../src/FindCopy.Core/Deletion.cs#L235).

Deletion assumes all links are gone if the known in-tree aliases were removed.
It does not account for remaining links outside the scanned roots, even when
`LinkCount` is available. The optimized enumeration path also does not populate
link counts.

The regression creates an external link to the selected copy and permanently
deletes the in-tree path. Observed: original link count 2, zero known aliases,
external link still present, and 4,096 reported freed bytes. Physical storage
has not been released. This also limits the pre-deletion group estimate.

Required correction: incorporate remaining-link information and report zero
physical savings or uncertainty when external links retain the object. Recycle
Bin moves likewise do not immediately release the file's allocation.

### R8 — Benchmark limitation: generated large files overflow

Location: [scenario D generator](../tools/FindCopy.Bench/Program.cs#L175).

Scenario D computes size with a 32-bit `int` and allocates a whole-file byte
array. `--scenario D --scale 8` fails instead of generating a 2 GiB file.
Consequently the generator cannot supply the required 10–100 GiB workload.
The `--path` mode can scan a separately prepared large dataset; it does not
repair this generator or establish that acceptance workloads were run.

## Additional code-review concerns requiring targeted validation

- **Path-based recycling:** [Deletion.cs](../src/FindCopy.Core/Deletion.cs#L206)
  closes the verified candidate handle before `MoveToRecycleBin(path)`. A
  concurrent replacement can change which file that path names. This interval
  is visible in code; native Windows race/recycling tests were not executed.
- **Enumeration fallback is narrower than the specification:**
  [EnumerateExtd](../src/FindCopy.Core/WindowsFileSystem.cs#L94) falls back for
  selected first-call unsupported codes or invalid first batches, but returns
  other errors without trying baseline enumeration. Errors after a reported
  batch need a deduplicated fallback policy. The documentation's broad automatic
  fallback claim is stronger than the implementation.
- **Follow-mode identity failure:** the
  [visited-set logic](../src/FindCopy.Core/ScanController.cs#L240) permits a
  directory when identity lookup fails. Reparse-follow mode therefore lacks a
  guaranteed cycle defense on that path. Default link skipping is implemented.
- **USN optimization is partial:** journal changes invalidate cached hashes,
  but `Execute` still enumerates every directory before applying the journal.
  The requirement to avoid re-enumerating unchanged files is not delivered.

These concerns are static findings or scope gaps, distinct from the seven
executed regression failures. They are not presented as successful Windows
reproductions.

## Coverage by specification section

| Sections | Implementation and evidence | Remaining acceptance limits |
| --- | --- | --- |
| 1–3, 6, 25 | Both modes, staged filtering, content semantics, direct small-file hashing; baseline tests verify unique sizes read zero bytes | Concurrency/version defects above prevent complete correctness |
| 4 | Unicode Win32 APIs, extended paths, longPathAware manifest, batch and baseline backends | Native Windows execution/fallback failures untested |
| 5, 29 | Struct array, name arena, directory table, fixed streaming buffers | 152-byte target miss; 5–10 million-file peak memory unmeasured |
| 7, 24 | Physical ID grouping and alias-aware logical savings; hard-link tests pass | External links and deletion savings R6; zero-byte list bypasses identity grouping |
| 8–10, 26 | Tunable threshold/samples/buffers, XXH3, native BLAKE3, staged Q1–Q5 | No production device tuning; >=1 GiB Q4/Q5 paths not covered by baseline fixtures/default benchmarks |
| 11 | Hash/exact distinction, mocked collision test, separate byte comparison, fresh pre-delete compare | R1 and R7; recycling race needs Windows validation |
| 12 | Before/after full-read snapshots, one retry | R2/R3; expensive quick/ADS stages lack equivalent full metadata snapshot checks |
| 13 | Default name-surrogate link skipping and optional visited IDs | Real junction test absent; unknown directory reparse handling/follow identity failure need validation |
| 14–15 | Cloud attribute filter/prompt, allocation fields, ordinary logical-content reading | Cloud test uses injected attributes; actual OneDrive, NTFS sparse/compressed, and EFS not validated |
| 16–17 | Physical storage profiling/domain keys, separate stage runs, bounded readers/global budget, BLAKE3 package | Mocked NVMe correctness is not measured autotuning; no device acceptance evidence |
| 18 | SQLite transactions, schema/hash/sampling versions, metadata validation, corrupt-cache recovery | R2; creation metadata is not stored; cache concurrency/version acceptance incomplete |
| 19 | Journal query/read, ID invalidation, reset/gap fallback | Fake journal tests only; still enumerates full tree |
| 20–23 | Error states, counters, cancellation checks, grouped results and aliases | R4; empty-file list has no physical-group model; checks below are reduced-scale |
| 27 | Baseline includes most numbered cases plus cache/deletion additions | Compressed case 14 absent; Windows-specific cases and original acceptance scales incomplete |
| 28 | A–D generation, real-path mode, CSV metrics; reduced benchmarks completed | R8; E–H hardware runs missing; peak RAM includes generator history |
| 30–32 | Core/UI separation and most named responsibilities, including later-phase layers | All invariants cannot be claimed while reproduced failures remain |

Cache and USN are later delivery phases; their absence or partial optimization
must be separated from baseline correctness. Here they are present, but their
presence does not establish full-spec acceptance.

## Benchmark evidence and limits

At scale 1 without cache:

| Scenario | Dataset | Content read | Groups |
| --- | --- | --- | --- |
| A | 20,000 unique-size files | 0 bytes | 0 |
| B | 200 files of 1.5 MiB, differing at start | 13,107,200 bytes, exactly 64 KiB/file | 0 |
| C | Four 64 MiB files differing near middle | 786,432 bytes, three 64 KiB stages/file | 0 |
| D | Four identical 256 MiB files | 1,074,528,256 bytes, samples plus full streams | 1 |

Warm B/C/D runs read zero content bytes and reported 100% cache hit rates.
On B, thresholds 256 KiB and 1 MiB both read 12.5 MiB in samples; a 4 MiB
threshold directly hashed all 300 MiB. These are OS-cache-affected smoke
measurements, not HDD/SATA/NVMe/network recommendations. The harness reports
process-lifetime peak working set, including dataset generation, so its peak
RAM cannot be interpreted as isolated scanner metadata memory.

Required original scales not exercised by the baseline: test 18 uses 3,000
files rather than millions; test 20 uses 48 MiB rather than 100+ GiB. The sparse
fixture does not explicitly enable NTFS sparse attributes on Windows. The
junction-loop test is guarded to run only on non-Windows hosts. Test 33 compares
backend results but does not require a nonzero fast-backend directory count.

## Next implementation work

1. Bind alias deletion/recycling to the verified object and revalidate opened
   identities; add native Windows concurrent-replacement tests.
2. Fix exact length/version validation and cached-result freshness; choose a
   conservative fallback when required metadata cannot be obtained.
3. Correct completeness and savings reporting.
4. Complete Windows filesystem tests, bounded large-file benchmark generation,
   and measured memory/device acceptance.
5. Decide the intended USN incremental scope and implement skipped enumeration
   if full section 19 optimization is required.

No production algorithms were changed as part of this import and audit.
