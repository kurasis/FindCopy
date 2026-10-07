using Microsoft.Data.Sqlite;

namespace FindCopy.Core;

/// <summary>Identity + metadata of a file used to look up and validate a cache entry (ТЗ §18).</summary>
public struct CacheKey
{
    public bool HasId;
    public ulong VolumeSerial;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public string Path;
    public long Size;
    public long LastWriteTicks;
    public long ChangeTicks;
    public long CreationTicks;
}

public sealed class CacheEntry
{
    public byte QMask;                    // bit (stage-1) set when Q[stage-1] is valid
    public readonly ulong[] Q = new ulong[5];
    public byte[]? FullHash;              // 32 bytes, BLAKE3-256
}

/// <summary>
/// Persistent fingerprint cache, an optimisation layer only (ТЗ §18). Any doubt about an entry is
/// a miss: identity alone is never trusted, size + LastWriteTime + ChangeTime must match exactly,
/// as must the sampling-scheme, hash-algorithm and schema versions. Writes go in transactions,
/// and only complete hashes are ever written, so a cancelled scan cannot corrupt the cache.
/// Not thread-safe: the scanner calls it from its orchestration thread only.
/// </summary>
public sealed class ScanCache : IDisposable
{
    public const int SchemaVersion = 3;
    public const int HashAlgorithmVersion = 1;      // 1 = XXH3_64 quick, BLAKE3-256 full
    private const int PruneAfterDays = 180;

    private readonly SqliteConnection _db;
    private readonly bool _caseInsensitivePaths;
    private SqliteCommand? _getById, _getByPath;

    public string FilePath { get; }

    private readonly int _sampleSize;

    /// <param name="sampleSize">Quick-sample size in bytes; cached quick hashes taken with another size are ignored.</param>
    public ScanCache(string path, int sampleSize = 64 * 1024)
    {
        FilePath = path;
        _sampleSize = sampleSize;
        _caseInsensitivePaths = OperatingSystem.IsWindows();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            InitSchema();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            // A damaged cache is just thrown away: it can only cost re-hashing, never a false duplicate.
            _db.Close();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { }
            _db.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            InitSchema();
        }
    }

    public static string DefaultPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = System.IO.Path.GetTempPath();
        return System.IO.Path.Combine(baseDir, "FindCopy", "cache.db");
    }

    private void InitSchema()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        string? ver = Scalar("SELECT value FROM meta WHERE key='schema';") as string;
        if (ver != SchemaVersion.ToString())
        {
            Exec("DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS usn;");
            Exec("INSERT OR REPLACE INTO meta(key,value) VALUES('schema','" + SchemaVersion + "');");
        }
        Exec(@"CREATE TABLE IF NOT EXISTS files(
                 id INTEGER PRIMARY KEY,
                 has_id INTEGER NOT NULL, vol INTEGER NOT NULL, fid_lo INTEGER NOT NULL, fid_hi INTEGER NOT NULL,
                 path TEXT NOT NULL, path_key TEXT NOT NULL,
                 size INTEGER NOT NULL, mtime INTEGER NOT NULL, ctime INTEGER NOT NULL, creation INTEGER NOT NULL,
                 sampling_ver INTEGER NOT NULL, hash_ver INTEGER NOT NULL, sample_size INTEGER NOT NULL,
                 qmask INTEGER NOT NULL, q1 INTEGER, q2 INTEGER, q3 INTEGER, q4 INTEGER, q5 INTEGER,
                 full BLOB, seen INTEGER NOT NULL);
               CREATE UNIQUE INDEX IF NOT EXISTS ix_files_id ON files(vol, fid_lo, fid_hi) WHERE has_id = 1;
               CREATE UNIQUE INDEX IF NOT EXISTS ix_files_path ON files(path_key) WHERE has_id = 0;
               CREATE TABLE IF NOT EXISTS usn(vol INTEGER PRIMARY KEY, journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL);");
        Exec($"DELETE FROM files WHERE seen < {Today() - PruneAfterDays};");
    }

    private static long Today() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;
    private string PathKey(string p) => _caseInsensitivePaths ? p.ToUpperInvariant() : p;

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar();
    }

    public bool TryGet(in CacheKey key, out CacheEntry? entry)
    {
        entry = null;
        SqliteCommand cmd;
        if (key.HasId)
        {
            _getById ??= Prepare("SELECT size, mtime, ctime, sampling_ver, hash_ver, qmask, q1, q2, q3, q4, q5, full, sample_size, creation FROM files WHERE has_id=1 AND vol=$a AND fid_lo=$b AND fid_hi=$c;", "$a", "$b", "$c");
            cmd = _getById;
            cmd.Parameters[0].Value = unchecked((long)key.VolumeSerial);
            cmd.Parameters[1].Value = unchecked((long)key.FileIdLow);
            cmd.Parameters[2].Value = unchecked((long)key.FileIdHigh);
        }
        else
        {
            _getByPath ??= Prepare("SELECT size, mtime, ctime, sampling_ver, hash_ver, qmask, q1, q2, q3, q4, q5, full, sample_size, creation FROM files WHERE has_id=0 AND path_key=$a;", "$a");
            cmd = _getByPath;
            cmd.Parameters[0].Value = PathKey(key.Path);
        }
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return false;
        // Strict validation: identity is not enough, metadata must be unchanged (ТЗ §18).
        if (r.GetInt64(0) != key.Size || r.GetInt64(1) != key.LastWriteTicks || r.GetInt64(2) != key.ChangeTicks || r.GetInt64(13) != key.CreationTicks) return false;
        if (r.GetInt32(3) != Tuning.SamplingSchemeVersion || r.GetInt32(4) != HashAlgorithmVersion) return false;
        // Quick hashes depend on the sample size; the full hash does not.
        var e = new CacheEntry { QMask = r.GetInt32(12) == _sampleSize ? (byte)r.GetInt32(5) : (byte)0 };
        for (int i = 0; i < 5; i++)
            if ((e.QMask & (1 << i)) != 0 && !r.IsDBNull(6 + i)) e.Q[i] = unchecked((ulong)r.GetInt64(6 + i));
            else e.QMask &= (byte)~(1 << i);
        if (!r.IsDBNull(11))
        {
            var blob = (byte[])r.GetValue(11);
            if (blob.Length == 32) e.FullHash = blob;
        }
        if (e.QMask == 0 && e.FullHash == null) return false;
        entry = e;
        return true;
    }

    private SqliteCommand Prepare(string sql, params string[] names)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var n in names) c.Parameters.Add(new SqliteParameter(n, null));
        c.Prepare();
        return c;
    }

    /// <summary>Writes (upserts) entries in one transaction. Existing values for the same metadata are merged.</summary>
    public void PutMany(IEnumerable<(CacheKey Key, CacheEntry Entry)> items)
    {
        using var tx = _db.BeginTransaction();
        using var del = _db.CreateCommand();
        del.Transaction = tx;
        using var ins = _db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"INSERT INTO files(has_id, vol, fid_lo, fid_hi, path, path_key, size, mtime, ctime, sampling_ver, hash_ver, qmask, q1, q2, q3, q4, q5, full, seen, sample_size, creation)
                            VALUES($hid, $vol, $lo, $hi, $path, $pk, $size, $mt, $ct, $sv, $hv, $qm, $q1, $q2, $q3, $q4, $q5, $full, $seen, $ss, $creation);";
        foreach (var n in new[] { "$hid", "$vol", "$lo", "$hi", "$path", "$pk", "$size", "$mt", "$ct", "$sv", "$hv", "$qm", "$q1", "$q2", "$q3", "$q4", "$q5", "$full", "$seen", "$ss", "$creation" })
            ins.Parameters.Add(new SqliteParameter(n, null));
        long today = Today();

        foreach (var (k, e0) in items)
        {
            var e = e0;
            // Merge with an existing entry for the very same metadata, then replace the row.
            if (TryGetInTx(k, tx, out var old) && old != null)
            {
                for (int i = 0; i < 5; i++)
                    if ((e.QMask & (1 << i)) == 0 && (old.QMask & (1 << i)) != 0) { e.Q[i] = old.Q[i]; e.QMask |= (byte)(1 << i); }
                e.FullHash ??= old.FullHash;
            }
            if (k.HasId)
            {
                del.CommandText = $"DELETE FROM files WHERE (has_id=1 AND vol={unchecked((long)k.VolumeSerial)} AND fid_lo={unchecked((long)k.FileIdLow)} AND fid_hi={unchecked((long)k.FileIdHigh)}) OR (has_id=0 AND path_key=$pk);";
            }
            else
            {
                del.CommandText = "DELETE FROM files WHERE has_id=0 AND path_key=$pk;";
            }
            del.Parameters.Clear();
            del.Parameters.AddWithValue("$pk", PathKey(k.Path));
            del.ExecuteNonQuery();

            var p = ins.Parameters;
            p[0].Value = k.HasId ? 1 : 0;
            p[1].Value = unchecked((long)k.VolumeSerial);
            p[2].Value = unchecked((long)k.FileIdLow);
            p[3].Value = unchecked((long)k.FileIdHigh);
            p[4].Value = k.Path;
            p[5].Value = PathKey(k.Path);
            p[6].Value = k.Size;
            p[7].Value = k.LastWriteTicks;
            p[8].Value = k.ChangeTicks;
            p[9].Value = Tuning.SamplingSchemeVersion;
            p[10].Value = HashAlgorithmVersion;
            p[11].Value = (int)e.QMask;
            for (int i = 0; i < 5; i++) p[12 + i].Value = (e.QMask & (1 << i)) != 0 ? unchecked((long)e.Q[i]) : DBNull.Value;
            p[17].Value = (object?)e.FullHash ?? DBNull.Value;
            p[18].Value = today;
            p[19].Value = _sampleSize;
            p[20].Value = k.CreationTicks;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private bool TryGetInTx(in CacheKey k, SqliteTransaction tx, out CacheEntry? e)
    {
        if (_getById != null) _getById.Transaction = tx;
        if (_getByPath != null) _getByPath.Transaction = tx;
        try { return TryGet(k, out e); }
        finally
        {
            if (_getById != null) _getById.Transaction = null;
            if (_getByPath != null) _getByPath.Transaction = null;
        }
    }

    /// <summary>Drops entries for files the USN journal reports as changed (ТЗ §19).</summary>
    public int InvalidateIds(ulong volume, IEnumerable<(ulong Lo, ulong Hi)> ids)
    {
        int n = 0;
        using var tx = _db.BeginTransaction();
        using var c = _db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "DELETE FROM files WHERE has_id=1 AND vol=$v AND fid_lo=$lo AND fid_hi=$hi;";
        c.Parameters.AddWithValue("$v", unchecked((long)volume));
        var lo = c.Parameters.Add(new SqliteParameter("$lo", 0L));
        var hi = c.Parameters.Add(new SqliteParameter("$hi", 0L));
        foreach (var (l, h) in ids)
        {
            lo.Value = unchecked((long)l);
            hi.Value = unchecked((long)h);
            n += c.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>Journal gap or reset: nothing on this volume can be vouched for any more.</summary>
    public int InvalidateVolume(ulong volume)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM files WHERE vol=$v; DELETE FROM usn WHERE vol=$v;";
        c.Parameters.AddWithValue("$v", unchecked((long)volume));
        return c.ExecuteNonQuery();
    }

    public bool TryGetUsnState(ulong volume, out ulong journalId, out long nextUsn)
    {
        journalId = 0; nextUsn = 0;
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT journal_id, next_usn FROM usn WHERE vol=$v;";
        c.Parameters.AddWithValue("$v", unchecked((long)volume));
        using var r = c.ExecuteReader();
        if (!r.Read()) return false;
        journalId = unchecked((ulong)r.GetInt64(0));
        nextUsn = r.GetInt64(1);
        return true;
    }

    public void SetUsnState(ulong volume, ulong journalId, long nextUsn)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT OR REPLACE INTO usn(vol, journal_id, next_usn) VALUES($v, $j, $n);";
        c.Parameters.AddWithValue("$v", unchecked((long)volume));
        c.Parameters.AddWithValue("$j", unchecked((long)journalId));
        c.Parameters.AddWithValue("$n", nextUsn);
        c.ExecuteNonQuery();
    }

    public void ClearUsnState(ulong volume)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM usn WHERE vol=$v;";
        c.Parameters.AddWithValue("$v", unchecked((long)volume));
        c.ExecuteNonQuery();
    }

    public long EntryCount => (long)(Scalar("SELECT COUNT(*) FROM files;") ?? 0L);

    public void Clear()
    {
        Exec("DELETE FROM files; DELETE FROM usn;");
        Exec("VACUUM;");
    }

    public void Dispose()
    {
        _getById?.Dispose();
        _getByPath?.Dispose();
        _db.Dispose();
    }

    /// <summary>Deletes the cache file entirely (used by the UI "clear cache" button).</summary>
    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
