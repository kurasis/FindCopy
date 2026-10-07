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
Nineteen injected acceptance cases compare incremental output against fresh
scans, including aliases, renames, resets, corruption, cancellation, and competing
checkpoints. A separate Windows case exercises the native journal.

Schema 5 stores the generation at the root checkpoint, rather than on every
listing row. Each directory read joins that checkpoint and requires the expected
generation in the same SQL statement. Reused listings stage membership markers
without copying their blobs. At commit, after checking the expected generation,
only changed/new blobs are written and unvisited directories are removed; the
checkpoint advances atomically. Concurrent commits still cannot mix snapshots
or overwrite a newer checkpoint. This avoids writing a whole unchanged tree
back to SQLite on every warm scan.

The UTF-16 codec validates the complete bounded, checksummed listing before
replay and passes name spans directly to the handler. It creates no per-entry
name strings during either validation or replay. Nineteen inventory cases
include forbidden-write triggers for unchanged blobs, a valid-checksum malformed
tail, and Unicode/long-name allocation checks. Valid-checksum invalid names,
boolean flags, sizes, and timestamps are treated as damaged listings. Their
live replacements and checkpoint commit atomically, so the next warm scan can
reuse the repaired inventory without content reads. Semantic format errors do
not leave a permanently uncommittable root snapshot.

Cache schema is now 5. Older schemas are discarded and rebuilt once, including
fingerprint entries. This changes cache persistence, not the recovery history.
