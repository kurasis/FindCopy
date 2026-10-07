using Microsoft.Data.Sqlite;

namespace FindCopy.Core;

internal sealed record InventoryState(string Generation, ulong Volume, ulong RootLow, ulong RootHigh,
    ulong Journal, long NextUsn);

public sealed partial class ScanCache
{
    private SqliteCommand? _inventoryGet, _inventoryStage;
    private void InitInventorySchema() => Exec(@"
        CREATE TABLE IF NOT EXISTS inventory_roots(
            root TEXT PRIMARY KEY, generation TEXT NOT NULL, vol INTEGER NOT NULL,
            lo INTEGER NOT NULL, hi INTEGER NOT NULL, journal INTEGER NOT NULL, next_usn INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS inventory_dirs(
            root TEXT NOT NULL, path TEXT NOT NULL,
            vol INTEGER NOT NULL, lo INTEGER NOT NULL, hi INTEGER NOT NULL, entries BLOB NOT NULL,
            PRIMARY KEY(root,path));");

    internal void BeginInventory() => Exec(@"
        CREATE TEMP TABLE IF NOT EXISTS inventory_stage(
            root TEXT NOT NULL, path TEXT NOT NULL, vol INTEGER NOT NULL,
            lo INTEGER NOT NULL, hi INTEGER NOT NULL, entries BLOB, PRIMARY KEY(root,path));
        DELETE FROM inventory_stage;");

    internal InventoryState? GetInventoryState(string root)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT generation,vol,lo,hi,journal,next_usn FROM inventory_roots WHERE root=$r;";
        c.Parameters.AddWithValue("$r", PathKey(root));
        using var row = c.ExecuteReader();
        return row.Read() ? new InventoryState(row.GetString(0), unchecked((ulong)row.GetInt64(1)),
            unchecked((ulong)row.GetInt64(2)), unchecked((ulong)row.GetInt64(3)),
            unchecked((ulong)row.GetInt64(4)), row.GetInt64(5)) : null;
    }

    internal byte[]? GetInventoryDirectory(string root, string generation, string path, (ulong Vol, ulong Lo, ulong Hi) id)
    {
        // The root generation is checked in the same statement as the blob read. A competing
        // committed snapshot therefore cannot contribute directories to an older generation.
        var c = _inventoryGet ??= Prepare(@"SELECT d.entries FROM inventory_dirs d
            JOIN inventory_roots r ON r.root=d.root WHERE d.root=$r AND r.generation=$g
            AND d.path=$p AND d.vol=$v AND d.lo=$l AND d.hi=$h;", "$r", "$p", "$v", "$l", "$h", "$g");
        SetDirectory(c, root, path, id);
        c.Parameters["$g"].Value = generation;
        return c.ExecuteScalar() as byte[];
    }

    internal void StageInventoryDirectory(string root, string path, (ulong Vol, ulong Lo, ulong Hi) id, byte[]? entries)
    {
        // NULL is a membership marker for a verified unchanged blob, not a copy of that blob.
        var c = _inventoryStage ??= Prepare("INSERT OR REPLACE INTO inventory_stage(root,path,vol,lo,hi,entries) VALUES($r,$p,$v,$l,$h,$e);",
            "$r", "$p", "$v", "$l", "$h", "$e");
        SetDirectory(c, root, path, id);
        c.Parameters["$e"].Value = (object?)entries ?? DBNull.Value;
        c.ExecuteNonQuery();
    }

    private void SetDirectory(SqliteCommand c, string root, string path, (ulong Vol, ulong Lo, ulong Hi) id)
    {
        c.Parameters["$r"].Value = PathKey(root);
        c.Parameters["$p"].Value = PathKey(path);
        c.Parameters["$v"].Value = unchecked((long)id.Vol);
        c.Parameters["$l"].Value = unchecked((long)id.Lo);
        c.Parameters["$h"].Value = unchecked((long)id.Hi);
    }

    internal bool CommitInventory(string root, InventoryState state, string? expectedGeneration)
    {
        // A generation binds every listing to one checkpoint. Concurrent scans cannot mix snapshots.
        using var tx = _db.BeginTransaction();
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.Parameters.AddWithValue("$r", PathKey(root));
        c.CommandText = "SELECT generation FROM inventory_roots WHERE root=$r;";
        if ((c.ExecuteScalar() as string) != expectedGeneration) return false;
        c.Parameters.AddWithValue("$g", state.Generation);
        c.Parameters.AddWithValue("$v", unchecked((long)state.Volume));
        c.Parameters.AddWithValue("$l", unchecked((long)state.RootLow));
        c.Parameters.AddWithValue("$h", unchecked((long)state.RootHigh));
        c.Parameters.AddWithValue("$j", unchecked((long)state.Journal));
        c.Parameters.AddWithValue("$n", state.NextUsn);
        c.CommandText = @"
            DELETE FROM inventory_dirs WHERE root=$r AND NOT EXISTS
                (SELECT 1 FROM inventory_stage s WHERE s.root=inventory_dirs.root AND s.path=inventory_dirs.path);
            INSERT INTO inventory_dirs(root,path,vol,lo,hi,entries)
                SELECT root,path,vol,lo,hi,entries FROM inventory_stage WHERE root=$r AND entries IS NOT NULL
                ON CONFLICT(root,path) DO UPDATE SET vol=excluded.vol,lo=excluded.lo,hi=excluded.hi,entries=excluded.entries;
            INSERT OR REPLACE INTO inventory_roots(root,generation,vol,lo,hi,journal,next_usn)
                VALUES($r,$g,$v,$l,$h,$j,$n);";
        c.ExecuteNonQuery();
        tx.Commit();
        return true;
    }
}
