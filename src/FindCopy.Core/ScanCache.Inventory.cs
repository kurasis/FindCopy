using Microsoft.Data.Sqlite;

namespace FindCopy.Core;

internal sealed record InventoryState(string Generation, ulong Volume, ulong RootLow, ulong RootHigh,
    ulong Journal, long NextUsn);

public sealed partial class ScanCache
{
    private void InitInventorySchema() => Exec(@"
        CREATE TABLE IF NOT EXISTS inventory_roots(
            root TEXT PRIMARY KEY, generation TEXT NOT NULL, vol INTEGER NOT NULL,
            lo INTEGER NOT NULL, hi INTEGER NOT NULL, journal INTEGER NOT NULL, next_usn INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS inventory_dirs(
            root TEXT NOT NULL, generation TEXT NOT NULL, path TEXT NOT NULL,
            vol INTEGER NOT NULL, lo INTEGER NOT NULL, hi INTEGER NOT NULL, entries BLOB NOT NULL,
            PRIMARY KEY(root,generation,path));");

    internal void BeginInventory() => Exec(@"
        CREATE TEMP TABLE IF NOT EXISTS inventory_stage(
            root TEXT NOT NULL, path TEXT NOT NULL, vol INTEGER NOT NULL,
            lo INTEGER NOT NULL, hi INTEGER NOT NULL, entries BLOB NOT NULL, PRIMARY KEY(root,path));
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
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT entries FROM inventory_dirs WHERE root=$r AND generation=$g AND path=$p AND vol=$v AND lo=$l AND hi=$h;";
        BindDirectory(c, root, path, id);
        c.Parameters.AddWithValue("$g", generation);
        return c.ExecuteScalar() as byte[];
    }

    internal void StageInventoryDirectory(string root, string path, (ulong Vol, ulong Lo, ulong Hi) id, byte[] entries)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT OR REPLACE INTO inventory_stage(root,path,vol,lo,hi,entries) VALUES($r,$p,$v,$l,$h,$e);";
        BindDirectory(c, root, path, id);
        c.Parameters.AddWithValue("$e", entries);
        c.ExecuteNonQuery();
    }

    private void BindDirectory(SqliteCommand c, string root, string path, (ulong Vol, ulong Lo, ulong Hi) id)
    {
        c.Parameters.AddWithValue("$r", PathKey(root));
        c.Parameters.AddWithValue("$p", PathKey(path));
        c.Parameters.AddWithValue("$v", unchecked((long)id.Vol));
        c.Parameters.AddWithValue("$l", unchecked((long)id.Lo));
        c.Parameters.AddWithValue("$h", unchecked((long)id.Hi));
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
            DELETE FROM inventory_dirs WHERE root=$r;
            INSERT INTO inventory_dirs(root,generation,path,vol,lo,hi,entries)
                SELECT root,$g,path,vol,lo,hi,entries FROM inventory_stage WHERE root=$r;
            INSERT OR REPLACE INTO inventory_roots(root,generation,vol,lo,hi,journal,next_usn)
                VALUES($r,$g,$v,$l,$h,$j,$n);";
        c.ExecuteNonQuery();
        tx.Commit();
        return true;
    }
}
