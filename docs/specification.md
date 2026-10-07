# Windows Duplicate File Scanner Specification

This is an English requirements digest of the supplied technical specification,
`duplicate_file_scanner_windows_TZ.md`. Section numbers retain traceability to
the original. Requirements are acceptance criteria, not implementation claims.

## 1. Goal

Build a high-performance Windows duplicate-file engine with `Current folder
only` and `Recursive` modes. Compare content independently of names, extensions,
and timestamps. Support millions of files and individual files tens or hundreds
of GiB in size without reading every file fully or loading entire files into RAM.
Handle storage characteristics, hard links, reparse points, sparse/compressed
files, cloud placeholders, access failures, and concurrent changes. Keep the
core extensible for persistent caching and NTFS USN Journal tracking.

## 2. Duplicate definition

A duplicate has the same byte sequence in its primary unnamed data stream.
Names, extensions, paths, timestamps, ACLs, attributes, and ownership do not
determine equality. Hard-link aliases of one object are not separate physical
copies. Alternate data streams are excluded from V1; a future ADS mode would
compare stream lists, names, lengths, and contents.

## 3. Pipeline

`Enumerate -> group by logical size -> reject unique sizes -> resolve hard-link
identity -> Q1 -> regroup -> Q2 -> regroup -> additional samples as needed ->
full BLAKE3 -> candidate groups -> optional exact byte comparison`.

Every subsequent stage operates only on groups containing at least two
independent file objects. Never perform content I/O on a unique-size file.

## 4. Enumeration

Use Unicode Win32 APIs and long-path-capable internal paths. The application
must be `longPathAware`. The compatible baseline is
`FindFirstFileExW` / `FindNextFileW`, preferably `FindExInfoBasic` with
`FIND_FIRST_EX_LARGE_FETCH` where supported. An optional batch backend using
`GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` may return identity,
size, allocation, attributes, reparse tags, timestamps, and names. Fall back to
the baseline on unsupported operations or backend errors.

## 5. Metadata layout

Use compact records in contiguous storage and a separate path pool/string arena.
Store `path_id`, logical size, allocation size when known, attributes, last write
time, optional reparse tag, optional volume/file identity, storage domain, scan
state, quick fingerprints, full hash, and error code. Avoid a separate heavy
heap object or per-optional-field allocation for each file.

## 6. Size grouping

Sort by logical size, traverse contiguous groups, and reject singletons without
content reads. Empty independent files may form duplicate groups but reclaim
zero bytes; optionally display them separately or hide them by default.

## 7. Hard links

Resolve surviving candidates by volume serial number and file ID. Represent
one physical file with multiple alias paths; enter it once in a duplicate
group. Optionally obtain link count through `FILE_STANDARD_INFO.NumberOfLinks`.
File IDs are filesystem-specific and reusable after deletion. If identity is
unavailable or invalid, continue scanning without that optimization; do not
treat a file ID alone as a permanent cache key.

## 8. Small files

Start with a tunable 1 MiB threshold. After size grouping and identity
resolution, hash files at or below the threshold directly with full BLAKE3,
without repeated sample/open/close cycles. Benchmark thresholds of 256 KiB,
1 MiB, and 4 MiB before selecting a measured default.

## 9. Large-file fingerprints

Use `XXH3_64` only to reject unequal candidates, never to establish equality.
Start with 64 KiB samples and regroup after each stage:

| Stage | Read | Eligibility |
| --- | --- | --- |
| Q1 | First sample | Large-file candidates |
| Q2 | Last sample | Q1 survivors |
| Q3 | Sample near 50% | Survivors at least 16 MiB |
| Q4 | Samples near 25% and 75%, each as a separate stage | Optional for survivors at least 1 GiB |

Keys retain size and prior fingerprints. Never read later samples before
earlier stages can reject candidates. Derive offsets deterministically from
size and sampling-scheme version; retain the version in cache metadata.

## 10. Full hash

Use streaming `BLAKE3-256`, initially with a benchmark-tunable 1–4 MiB buffer.
Use `FILE_FLAG_SEQUENTIAL_SCAN` for sequential reads and buffered I/O as the
baseline. Do not default to unbuffered I/O or memory mapping. Experimental
backends require benchmarks on HDD, SATA SSD, NVMe, and network shares. Group
full hashes by logical size and digest.

## 11. Hash and exact verification

Matching size and BLAKE3 produces `HASH_MATCH`. Only a successful byte-for-byte
comparison produces `EXACT_MATCH`. A separate `ExactVerifier` streams each
candidate against a reference and stops a pair at the first mismatch. Require
exact verification for Strict/Exact mode and before destructive actions,
automatic deletion, or replacing a copy with a hard link.

## 12. Concurrent modification

Take metadata snapshots for expensive stages. Before and after full hashing,
compare file ID when available, logical size, last write time, and change time
when available. Discard hashes after changes and report `CHANGED_DURING_SCAN`.
At most one automatic retry is allowed; exclude a repeatedly changing file
from current results. Do not normally lock files against writes for the scan.

## 13. Reparse points

By default, do not follow directory junctions, symlinks, mount points, or other
directory reparse points. File symlinks do not count as separate physical
copies. An advanced follow mode requires a `VisitedDirectoryIdentitySet` to
prevent cycles and repeated traversal.

## 14. Cloud placeholders

Recognize `FILE_ATTRIBUTE_OFFLINE`, `FILE_ATTRIBUTE_RECALL_ON_OPEN`, and
`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`. Default to skipping non-local or
recall-required content with `CLOUD_CONTENT_NOT_LOCAL`. An advanced
`Include online-only files` option must warn about downloads, network use,
and local space. Do not classify cloud placeholders as ordinary symlinks
merely because they use reparse-point mechanisms.

## 15. Sparse, compressed, and encrypted files

Compare logical content, including sparse and NTFS compressed files. Distinguish
logical size from allocated size. Label potential physical savings as
`Estimated reclaimable disk space`; actual savings depend on filesystem
features. Read EFS files through ordinary APIs when authorized; otherwise
report `ACCESS_DENIED` and exclude them from duplicate results.

## 16. Storage scheduling

Assign files to storage domains. Prefer mapping volumes sharing a physical
disk to one domain; use a volume fallback if mapping is unavailable. Detect
seek characteristics through Windows storage APIs when possible. Initial
large-reader limits are HDD/seek-penalty: 1, SSD: 2, fast NVMe: tunable 2–4,
network/unknown: 1. Validate limits by benchmark. Prefer separate queues for
small sample and full sequential tasks. CPU core count must not determine
simultaneous file reads.

## 17. CPU scheduling

Separate I/O and hashing scheduling logically with one global CPU budget.
Prevent multiplying storage workers by BLAKE3 internal threads. Use the
official optimized BLAKE3 implementation with SIMD. Enable internal hashing
parallelism only after measured CPU bottlenecks justify it.

## 18. Persistent cache

Keep `DuplicateScanCache` separate from the core, preferably backed by a
transactional embedded store such as SQLite. Store volume/file identity,
latest path, size, creation/change/write metadata, quick fingerprints, BLAKE3,
sampling version, hash version, and schema version. Validate identity together
with metadata; use weaker path/size/timestamp validation if identity is absent.
Any uncertainty requires rehashing rather than trusting an entry.

## 19. NTFS USN Journal

Add USN optimization after a stable scanner/cache. Retain journal ID and last
processed USN per volume. Use journal changes only for invalidation and change
tracking, never as a content signature. Fall back to ordinary scanning after
journal replacement, missing old records, unavailability, or inconsistency.
The scanner must remain correct without USN or cache.
Use persisted inventory and journal deltas to avoid enumerating unchanged
directories. Enumerate normally whenever the previous inventory or journal
interval cannot be trusted.

## 20. Errors

Individual failures must not abort the scan. Support at least `OK`,
`ACCESS_DENIED`, `FILE_NOT_FOUND_DURING_SCAN`, `SHARING_VIOLATION`, `IO_ERROR`,
`CHANGED_DURING_SCAN`, `CLOUD_CONTENT_NOT_LOCAL`, `REPARSE_SKIPPED`,
`UNSUPPORTED`, and `CANCELLED`. Report checked, skipped, inaccessible, and
changed counts. With exclusions, say `No duplicates found among successfully
scanned files` rather than implying the complete tree was checked.

## 21. Cancellation

Check cancellation between directory entries, files, streaming blocks, and
pipeline stages. Close handles, stop new jobs, allow safe in-flight I/O to
finish, and preserve transactional cache integrity. Never save a partial full
hash as complete.

## 22. Progress and telemetry

Expose structured progress outside the UI: directories scanned, files and
logical bytes discovered, unique-size files rejected, hard-link aliases,
quick-hash files/bytes, full-hash files/bytes, cache hits/misses, hash/exact
groups, skipped files, and error files. Measure read amplification as actual
content bytes read divided by total logical bytes discovered.

## 23. Result model

Groups expose ID, logical size, file count, unique physical-file count, hash,
verification state, estimated reclaimable size, and members. Files expose path,
logical size, allocated size when available, last write time, internal physical
identity, alias count, and status. Display aliases separately and distinguish
`HASH_MATCH` from `EXACT_MATCH`.

## 24. Savings

Logical reclaimable bytes equal logical size times (unique physical files
minus one), not the raw path count minus one. Estimate physical savings using
allocation information for the relevant copies. An alias alone does not free
the underlying object's storage while another link remains. Empty-file savings
are zero.

## 25. Prohibited shortcuts

Do not use names/extensions to determine duplicates, hash every discovered
file fully, load whole files, create a thread per file, overload HDDs, count
aliases as independent copies, follow reparse directories by default, hydrate
cloud files without consent, trust persistent file IDs without validation,
treat XXH3 as proof, or act destructively based only on quick hashes.

## 26. Central tuning

Centralize configurable thresholds, sample sizes, offsets, streaming buffers,
and storage concurrency without changing algorithm/cache formats. Starting
values are 1 MiB small-file threshold, 64 KiB samples, 1–4 MiB streaming buffers,
and reader limits of 1/2/up to 4 for HDD/SSD/NVMe. Measure before final tuning.

## 27. Required correctness tests

1. Identical content with different names, extensions, and timestamps matches.
2. Equal sizes with different initial bytes are rejected by Q1.
3. Equal size/start with different endings are rejected by Q2.
4. A difference outside all sampled regions is detected by full hashing.
5. A mocked hash collision is rejected by exact byte verification.
6. Two hard links do not count as two reclaimable copies.
7. A hard link plus an independent copy yields one redundant physical copy.
8. A junction cycle cannot loop indefinitely.
9. A file symlink is not an additional physical copy by default.
10. Modification during full hashing produces `CHANGED_DURING_SCAN`.
11. Deletion during scanning is reported and scanning continues.
12. Access denial is reported and scanning continues.
13. Sparse and regular files with equal logical content match.
14. Compressed and uncompressed files with equal logical content match.
15. Empty files form groups with zero reclaimable bytes.
16. Unicode paths work.
17. Paths longer than 260 characters work.
18. Millions of unique-size files cause essentially no content I/O.
19. Online-only files are not hydrated by default.
20. Cancellation during a 100+ GiB read terminates safely without cache damage.

## 28. Benchmarks

Provide a dedicated harness: A, one million mostly unique-size files with near
zero content reads; B, 100,000 equal-size files differing at the start with
roughly one sample read per large candidate; C, large files with equal
headers/footers but different middles; D, real 10–100 GiB duplicates; E, HDD
reader concurrency 1/2/4; F, SATA SSD; G, NVMe; H, network share.
Record elapsed time, files/s, metadata enumeration rate, bytes read, MB/s,
CPU usage, peak RAM, cache hit rate, and read amplification. Select concurrency
and thresholds from measurements.

## 29. Memory acceptance

Memory scales with compact metadata, path storage, and a fixed number of
worker buffers, not aggregate file sizes. Target approximately at most 128
bytes of fixed metadata per file, excluding paths and database cache, with
predictable behavior at 5–10 million files. External sorting or disk-backed
metadata is a possible later extension, not the initial baseline.

## 30. Delivery priorities

1. Correctness core: enumeration, grouping, sampling, BLAKE3, change detection,
   errors, cancellation.
2. Windows filesystem correctness: hard links, reparse points, long paths,
   sparse/compressed files, cloud placeholders.
3. Storage detection, scheduling, and measured tuning.
4. Persistent cache.
5. NTFS USN incremental scanning.
6. Strict verification/destructive-action safety when deletion is a product
   feature. Section 11 still defines verification requirements for exact mode
   and all destructive actions.

## 31. Architecture

`ScanController -> DirectoryEnumerator -> FileMetadataStore -> SizeGrouper ->
PhysicalIdentityResolver -> QuickFingerprintEngine -> FullHashEngine ->
DuplicateGroupBuilder -> ExactVerifier`.

Separate `StorageProfiler`, `IOScheduler`, `ScanCache`, `USNTracker`,
`ProgressReporter`, and `ErrorCollector`. Stages have explicit states and are
independent of UI; UI contains no filesystem/hash logic.

## 32. Readiness

Unique-size files receive no content reads; quick hashes never authorize
destructive actions; aliases do not inflate savings; traversal cannot cycle;
large files stream; modified files cannot become false confirmed duplicates;
cloud content is opt-in; individual errors do not stop scanning; cache and USN
are optional optimizations; HDD reads remain conservative; actual bytes and
stage effectiveness are measurable.

The performance invariant is to delay reading content while a cheaper stage
can still exclude the file. The correctness invariant is that a quick
fingerprint alone never proves byte equality.
