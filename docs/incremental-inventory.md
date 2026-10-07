# Incremental Directory Inventory

With caching and USN enabled, normal Windows traversal can reuse SQLite directory
listings instead of enumerating unchanged directories. Follow-reparse mode uses
ordinary enumeration. Backends without complete bulk file identities refresh
those listings normally, because journal changes to another hard-link alias
cannot otherwise be matched safely.

Each root snapshot records its volume/root identity, journal identity, checkpoint,
and generation. The checkpoint is captured before enumeration. Journal deltas
dirty parent directories; changed file IDs also refresh every listing containing
an alias of that object. Renamed directory paths cannot reuse a listing saved
under their previous path. Raw listings retain policy-excluded entries, so
changing hidden/system/cloud or recursion options does not hide newly eligible
files. Only directories visited under the current options are retained.

Listings are bounded binary UTF-16 blobs with version, count, and checksum.
Validation completes before replay starts. A failed read, malformed/unsupported
journal record, journal gap/reset, invalid identity, or damaged listing falls
back to live enumeration. Root snapshots are staged in a connection-local SQLite
temporary table and committed with their checkpoint in one transaction only
after the scan succeeds. Cancellation and incomplete enumeration do not commit
partial snapshots. Generations prevent mixing or overwriting concurrent scans.

This is an enumeration optimization, not a content signature. Cached hashes and
emitted duplicate members still receive live version checks. Ordinary scanning
works with inventory, journal, or cache unavailable.

Telemetry distinguishes directories considered by the traversal from actual
native enumeration: `InventoryDirectoriesReused`, `InventoryEntriesReused`,
`InventoryRootsRebuilt`, and the native fast/fallback directory counters.
Fifteen injected acceptance cases compare incremental output against fresh
scans, including aliases, renames, resets, corruption, cancellation, and competing
checkpoints. A separate Windows case exercises the native journal.

Cache schema is now 4. Older schemas are discarded and rebuilt.
