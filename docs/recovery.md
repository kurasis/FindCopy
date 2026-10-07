# Original-path Recovery

Use **Восстановить…** in the main window to open FindCopy's deletion history.
Select an entry, click **Восстановить**, and confirm its original path. The
application returns the recorded file object to that name, then invalidates
the displayed scan results so the next search reflects restored membership.
It does not overwrite an existing file or recreate missing parent directories.

History is per user in `%LOCALAPPDATA%\FindCopy\recovery`, with one versioned
JSON entry per staged file or hard-link alias. An entry is flushed to disk
before handle-bound staging. Successful recycling records the actual `$R`
path returned by the shell and a SHA-256 digest of its corresponding `$I`
metadata. The short staging directory can then be removed without losing
the original path. A failed shell move remains a recoverable staged entry.
Completed restores remain marked in history across application restarts.
Malformed entries are reported and retained instead of silently discarded.
If a crash interrupts the update after the shell move, the flushed staging
entry locates its unique recorded staging path in the current user's bin.
Recovery then checks the actual file version and metadata before adopting the
result. Ambiguous or uncertain matches are refused.

Recovery requires the recorded volume/file identity, creation time, length,
and last-write time to match the opened object. Rename-induced change times
are deliberately excluded. The default restore path does not hash/read the
whole file. Concurrent writable handles block restoration. Recorded recycled
paths must lie in the current user's local Recycle Bin, and both the staged
path/size in `$I` and its entire metadata digest must match the saved history.
The service opens the actual source and metadata objects without write/delete
sharing, validates them, and renames the source by its handle.

Every destination ancestor is opened as a reparse object and held without
delete sharing. Junction/symlink redirection and another volume are refused.
The destination directory handle anchors the native rename, which explicitly
disallows replacement. Long paths, original filenames longer than the staging
name, alternate NTFS streams, and hard-link relationships survive because this
is a same-volume rename of the recorded object. After a recycled data file has
returned, only its verified `$I` handle is deleted; unrelated bin entries stay.

An emptied bin, modified/replaced source, missing original directory, changed
metadata, permission failure, or destination conflict produces an explicit
failure and retains the history for investigation or retry. If the rename
succeeds but history/metadata cleanup fails, the outcome says the file was
restored and describes the remaining cleanup, avoiding an incorrect failure
claim. Such cleanup may need manual attention before the history reflects it.

This covers deletions recorded by the new implementation. Older deletions with
no saved recovery history cannot be reconstructed automatically. Permanent
deletions have no recovery entry. Explorer's Restore action still targets the
short staging location; original-path recovery is an application command.
Cache clearing does not erase deletion history or empty the Recycle Bin.

Native and desktop acceptance exercise recycling/restoration, long Unicode
paths, ADS, alias relationships, no-overwrite conflicts, missing directories,
junctions, modified/replaced objects, changed bin metadata, and open writers.
The published executable also exercises the real recovery window under a
non-admin account. [CI run 37617699942](https://github.com/kurasis/FindCopy/actions/runs/37617699942)
passed the native interrupted-journal recovery gate, Windows core 75/75, and
standard-user desktop 13/13; [filtered evidence](validation/windows-recovery-acceptance.txt)
records the assertions.
