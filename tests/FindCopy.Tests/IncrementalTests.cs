using FindCopy.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;

static class IncrementalTests
{
    public static void Run(Action<string, Action> test, string parent)
    {
        test("I1 unchanged inventory avoids directory enumeration and content reads", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            var r = f.Scan();
            Require(f.Fs.Enumerated.Count == 0 && r.Counters.InventoryDirectoriesReused == 3 &&
                r.Counters.ContentBytesRead == 0, "unchanged directories should be replayed, not enumerated");
            f.SameAsFresh(r);
        });
        test("I2 only modified parents are enumerated", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            string path = Path.Combine(f.Root, "x", "a");
            File.AppendAllText(path, "changed"); f.Fs.Change(path);
            var r = f.Scan();
            Require(f.Fs.Enumerated.SequenceEqual(new[] { Path.GetDirectoryName(path)! }), "only x should be refreshed");
            f.SameAsFresh(r);
        });
        test("I3 creation and deletion update inventory membership", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            string deleted = Path.Combine(f.Root, "x", "b"); f.Fs.Change(deleted); File.Delete(deleted);
            string added = Path.Combine(f.Root, "y", "new"); File.Copy(Path.Combine(f.Root, "y", "a"), added); f.Fs.Change(added);
            f.SameAsFresh(f.Scan());
        });
        test("I4 a changed hard link refreshes every cached alias parent", () =>
        {
            using var f = new Fixture(parent);
            string source = Path.Combine(f.Root, "x", "a");
            Native.HardLink(source, Path.Combine(f.Root, "y", "alias")); f.Seed();
            File.AppendAllText(source, "new length"); f.Fs.Change(source);
            var r = f.Scan();
            Require(f.Fs.Enumerated.Count == 2, "both alias parents must refresh"); f.SameAsFresh(r);
        });
        test("I5 renamed directories refresh their descendant paths", () =>
        {
            using var f = new Fixture(parent);
            string nested = Path.Combine(f.Root, "x", "nested"); Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "a"), "nested pair"); File.WriteAllText(Path.Combine(nested, "b"), "nested pair");
            f.Seed(); string original = Path.Combine(f.Root, "x"); f.Fs.Change(original);
            Directory.Move(original, Path.Combine(f.Root, "renamed"));
            var r = f.Scan(); f.SameAsFresh(r);
            Require(r.Groups.SelectMany(g => g.Files).All(x => !x.Path.StartsWith(original + Path.DirectorySeparatorChar)), "old paths retained");
        });
        test("I6 journal replacement requires complete enumeration", () =>
        {
            using var f = new Fixture(parent); f.Seed(); f.Fs.Journal++;
            var r = f.Scan(); Require(f.Fs.Enumerated.Count == 3 && r.Counters.InventoryRootsRebuilt == 1, "reset reused an inventory"); f.SameAsFresh(r);
        });
        test("I7 journal gaps and failed interval reads require complete enumeration", () =>
        {
            using var f = new Fixture(parent); f.Seed(); f.Fs.Next = 100; f.Fs.Lowest = 100;
            Require(f.Scan().Counters.InventoryDirectoriesReused == 0, "gap reused an inventory");
            f.Fs.Next++; f.Fs.FailRead = true;
            Require(f.Scan().Counters.InventoryDirectoriesReused == 0, "failed interval reused an inventory");
        });
        test("I8 unavailable journals use ordinary scanning", () =>
        {
            using var f = new Fixture(parent); f.Seed(); f.Fs.Available = false;
            var r = f.Scan(); Require(f.Fs.Enumerated.Count == 3, "journal unavailable"); f.SameAsFresh(r);
        });
        test("I9 entries without bulk IDs are never reused", () =>
        {
            using var f = new Fixture(parent); f.Fs.HideEntryIds = true; f.Seed();
            var r = f.Scan(); Require(f.Fs.Enumerated.Count == 2, "file entries without IDs must be refreshed"); f.SameAsFresh(r);
        });
        test("I10 incomplete enumeration does not advance the inventory checkpoint", () =>
        {
            using var f = new Fixture(parent); f.Seed(); long old = f.Checkpoint();
            string path = Path.Combine(f.Root, "x", "a"); f.Fs.Change(path);
            f.Fs.FailDirectory = Path.GetDirectoryName(path);
            Require(f.Scan().HasUncheckedFiles && f.Checkpoint() == old, "partial inventory committed");
            f.Fs.FailDirectory = null; f.SameAsFresh(f.Scan());
        });
        test("I11 cancellation does not commit a partially staged inventory", () =>
        {
            using var f = new Fixture(parent); f.Seed(); long old = f.Checkpoint();
            f.Fs.Change(Path.Combine(f.Root, "x", "a"));
            using var cts = new CancellationTokenSource(); f.Fs.AfterEnumeration = _ => cts.Cancel();
            bool cancelled = false;
            try { f.Scan(ct: cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && f.Checkpoint() == old, "cancelled inventory committed");
            f.Fs.AfterEnumeration = null; f.SameAsFresh(f.Scan());
        });
        test("I12 damaged listing payloads fall back before emitting entries", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            using (var db = new SqliteConnection("Data Source=" + f.Cache + ";Pooling=False"))
            {
                db.Open(); using var c = db.CreateCommand();
                c.CommandText = "UPDATE inventory_dirs SET entries=X'01020304';"; c.ExecuteNonQuery();
            }
            var r = f.Scan(); Require(f.Fs.Enumerated.Count == 3, "corrupt payload reused"); f.SameAsFresh(r);
        });
        test("I13 switching recursion cannot reuse an incomplete subtree", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            Require(f.Scan(recursive: false).Counters.FilesDiscovered == 0, "nonrecursive scan descended");
            var r = f.Scan(); Require(f.Fs.Enumerated.Count == 2, "missing child listings not refreshed"); f.SameAsFresh(r);
        });
        test("I14 a newer concurrent checkpoint cannot be overwritten", () =>
        {
            using var f = new Fixture(parent); f.Seed(); string generation = "";
            f.Fs.Change(Path.Combine(f.Root, "x", "a"));
            f.Fs.AfterEnumeration = _ =>
            {
                f.Fs.AfterEnumeration = null;
                using var cache = new ScanCache(f.Cache); cache.BeginInventory();
                var old = cache.GetInventoryState(f.Root)!; generation = Guid.NewGuid().ToString("N");
                Require(cache.CommitInventory(f.Root, old with { Generation = generation }, old.Generation), "concurrent commit");
            };
            f.Scan(); using (var cache = new ScanCache(f.Cache))
                Require(cache.GetInventoryState(f.Root)!.Generation == generation, "newer snapshot was overwritten");
            f.SameAsFresh(f.Scan());
        });
        test("I16 unchanged inventory advances checkpoint without rewriting blobs", () =>
        {
            using var f = new Fixture(parent); f.Seed(); long old = f.Checkpoint();
            using (var db = new SqliteConnection("Data Source=" + f.Cache + ";Pooling=False"))
            {
                db.Open(); using var c = db.CreateCommand();
                c.CommandText = @"CREATE TRIGGER no_blob_update BEFORE UPDATE ON inventory_dirs BEGIN SELECT RAISE(ABORT,'unexpected blob rewrite'); END;
                    CREATE TRIGGER no_blob_insert BEFORE INSERT ON inventory_dirs BEGIN SELECT RAISE(ABORT,'unexpected blob insert'); END;
                    CREATE TRIGGER no_blob_delete BEFORE DELETE ON inventory_dirs BEGIN SELECT RAISE(ABORT,'unexpected blob delete'); END;";
                c.ExecuteNonQuery();
            }
            f.Fs.Next++;
            var r = f.Scan();
            Require(f.Checkpoint() > old && r.Counters.CacheNote == null && f.Fs.Enumerated.Count == 0,
                "warm scan must advance generation without writing listing blobs");
            f.SameAsFresh(r);
        });
        test("I17 checksummed malformed listings fall back without partial replay", () =>
        {
            using var f = new Fixture(parent); f.Seed();
            using (var db = new SqliteConnection("Data Source=" + f.Cache + ";Pooling=False"))
            {
                db.Open(); using var c = db.CreateCommand();
                c.CommandText = "SELECT path,entries FROM inventory_dirs WHERE path=$p;";
                string p = Path.Combine(f.Root, "x");
                c.Parameters.AddWithValue("$p", OperatingSystem.IsWindows() ? p.ToUpperInvariant() : p);
                byte[] bytes;
                using (var row = c.ExecuteReader()) { Require(row.Read(), "cached leaf missing"); bytes = (byte[])row.GetValue(1); }
                // Damage the second record, retaining a valid checksum and a valid first record.
                int second = 8 + 68 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(second), ushort.MaxValue);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8),
                    System.IO.Hashing.XxHash3.HashToUInt64(bytes.AsSpan(0, bytes.Length - 8)));
                c.CommandText = "UPDATE inventory_dirs SET entries=$e WHERE path=$p;";
                c.Parameters.AddWithValue("$e", bytes); c.ExecuteNonQuery();
            }
            var r = f.Scan();
            Require(f.Fs.Enumerated.Count == 1 && r.Counters.FilesDiscovered == 4, "malformed tail duplicated emitted entries");
            f.SameAsFresh(r);
        });
        test("I19 invalid listing names, flags, and metadata heal on the next scan", () =>
        {
            foreach (string damage in new[] { "name", "directory flag", "identity flag", "size", "timestamp" })
            {
                using var f = new Fixture(parent); f.Seed(); long old = f.Checkpoint();
                using (var db = new SqliteConnection("Data Source=" + f.Cache + ";Pooling=False"))
                {
                    db.Open(); using var c = db.CreateCommand();
                    c.CommandText = "SELECT entries FROM inventory_dirs WHERE path=$p;";
                    string p = Path.Combine(f.Root, "x");
                    c.Parameters.AddWithValue("$p", OperatingSystem.IsWindows() ? p.ToUpperInvariant() : p);
                    byte[] bytes = (byte[])c.ExecuteScalar()!;
                    int second = 8 + 68 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
                    int metadata = second + 2 + 2 * BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(second));
                    switch (damage)
                    {
                        case "name": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(second + 2), '/'); break;
                        case "directory flag": bytes[metadata] = 2; break;
                        case "identity flag": bytes[metadata + 25] = 2; break;
                        case "size": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(metadata + 9), -1); break;
                        case "timestamp": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(metadata + 17), -1); break;
                    }
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8),
                        System.IO.Hashing.XxHash3.HashToUInt64(bytes.AsSpan(0, bytes.Length - 8)));
                    c.CommandText = "UPDATE inventory_dirs SET entries=$e WHERE path=$p;";
                    c.Parameters.AddWithValue("$e", bytes); c.ExecuteNonQuery();
                }
                f.Fs.Next++;
                var refreshed = f.Scan();
                Require(f.Fs.Enumerated.Count == 1 && refreshed.Counters.FilesDiscovered == 4 &&
                    refreshed.Counters.CacheNote == null && f.Checkpoint() > old, damage + " prevented atomic cache repair");
                f.SameAsFresh(refreshed);
                var warm = f.Scan();
                Require(f.Fs.Enumerated.Count == 0 && warm.Counters.InventoryDirectoriesReused == 3 &&
                    warm.Counters.ContentBytesRead == 0, damage + " remained damaged after refresh");
                f.SameAsFresh(warm);
            }
        });
        test("I18 inventory span codec preserves Unicode and avoids per-entry allocations", () =>
        {
            using var listing = new DirectoryListing();
            var e = new EntryInfo { HasIdentity = true, VolumeSerial = 1, FileIdLow = 2, Size = 1, LastWriteTicks = 1 };
            string name = "данные-🗂️-" + new string('x', 600);
            for (int i = 0; i < 1000; i++) listing.Append(name, e);
            byte[] bytes = listing.Finish()!; var changed = new HashSet<(ulong, ulong)>();
            Require(DirectoryListing.IsReusable(bytes, changed, default, out _), "valid Unicode listing rejected");
            DirectoryListing.Replay(bytes, (ReadOnlySpan<char> _, in EntryInfo _) => { }, default);
            long before = GC.GetAllocatedBytesForCurrentThread(); int seen = 0;
            Require(DirectoryListing.IsReusable(bytes, changed, default, out int count) && count == 1000, "record count");
            DirectoryListing.Replay(bytes, (ReadOnlySpan<char> n, in EntryInfo _) => { Require(n.SequenceEqual(name), "UTF-16 name changed"); seen++; }, default);
            Require(seen == 1000 && GC.GetAllocatedBytesForCurrentThread() - before < 4096, "codec allocated per-entry names");
        });
        test("I15 USN packets validate parent identities, bounds, and versions atomically", () =>
        {
            byte[] record = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(record, 64);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 7); BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 8);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 100);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), 2); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
            record[60] = (byte)'a';
            byte[] packet = new byte[72]; BinaryPrimitives.WriteInt64LittleEndian(packet, 164); record.CopyTo(packet, 8);
            var ids = new HashSet<(ulong, ulong)>(); var changes = new List<UsnChange>();
            Require(UsnRecordParser.Parse(packet, 90, 150, ids, changes, out _) && changes.Single() == new UsnChange(7, 0, 8, 0), "V2 packet");
            ids.Clear(); changes.Clear();
            byte[] bad = new byte[packet.Length + 8]; packet.CopyTo(bad, 0);
            Require(!UsnRecordParser.Parse(bad, 90, 150, ids, changes, out _) && ids.Count == 0 && changes.Count == 0, "partial packet leaked IDs");
            packet[12] = 9;
            Require(!UsnRecordParser.Parse(packet, 90, 150, ids, changes, out _), "unknown version accepted");
        });
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _home;
        public string Root { get; }
        public string Cache { get; }
        public JournalFs Fs { get; } = new();
        public Fixture(string parent)
        {
            _home = Path.Combine(parent, "inventory-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_home, "scan"); Cache = Path.Combine(_home, "cache.db");
            foreach (string sub in new[] { "x", "y" })
            {
                string d = Path.Combine(Root, sub); Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "a"), "pair " + sub); File.WriteAllText(Path.Combine(d, "b"), "pair " + sub);
            }
        }
        public ScanResult Scan(bool recursive = true, CancellationToken ct = default)
        {
            Fs.Enumerated.Clear();
            return new ScanController(Fs).RunAsync(new ScanOptions { Roots = new[] { Root }, CachePath = Cache, Recursive = recursive }, ct).GetAwaiter().GetResult();
        }
        public void Seed() => Scan();
        public long Checkpoint() { using var cache = new ScanCache(Cache); return cache.GetInventoryState(Root)!.NextUsn; }
        public void SameAsFresh(ScanResult result)
        {
            var fresh = new ScanController().RunAsync(new ScanOptions { Roots = new[] { Root } }).GetAwaiter().GetResult();
            static string Signature(ScanResult r) => string.Join("|", r.Groups.Select(g => g.LogicalSize + ":" + string.Join(",",
                g.Files.SelectMany(f => new[] { f.Path }.Concat(f.HardLinkAliases)).Order())).Order());
            Require(Signature(result) == Signature(fresh) && result.Counters.FilesDiscovered == fresh.Counters.FilesDiscovered, "incremental result differs from full scan");
        }
        public void Dispose() => Directory.Delete(_home, true);
    }

    private sealed class JournalFs : IFileSystem, IUsnInventorySource
    {
        private readonly IFileSystem _inner = FileSystemBase.CreateDefault();
        private readonly List<(long Usn, UsnChange Change)> _changes = new();
        public ulong Journal = 7;
        public long Next = 10, Lowest;
        public bool Available = true, FailRead, HideEntryIds;
        public string? FailDirectory;
        public Action<string>? AfterEnumeration;
        public readonly List<string> Enumerated = new();
        public void Change(string path)
        {
            (ulong Vol, ulong Lo, ulong Hi) file;
            if (Directory.Exists(path)) _inner.TryGetDirectoryIdentity(path, out file);
            else { _inner.GetIdentity(path, out var id); file = (id.VolumeSerial, id.FileIdLow, id.FileIdHigh); }
            _inner.TryGetDirectoryIdentity(Path.GetDirectoryName(path)!, out var parent);
            _changes.Add((Next++, new UsnChange(file.Lo, file.Hi, parent.Lo, parent.Hi)));
        }
        public string NormalizeRoot(string path) => _inner.NormalizeRoot(path);
        public FileStatus EnumerateDirectory(string path, DirEntryHandler handler, CancellationToken ct, out string? error)
        {
            Enumerated.Add(path);
            if (path == FailDirectory) { error = "injected failure"; return FileStatus.AccessDenied; }
            var status = _inner.EnumerateDirectory(path, (ReadOnlySpan<char> name, in EntryInfo entry) =>
            {
                var e = entry;
                if (!entry.IsDirectory && !HideEntryIds && _inner.GetIdentity(Path.Combine(path, name.ToString()), out var id) == FileStatus.Ok)
                {
                    e.HasIdentity = id.Valid; e.VolumeSerial = id.VolumeSerial; e.FileIdLow = id.FileIdLow; e.FileIdHigh = id.FileIdHigh;
                    e.LastWriteTicks = id.LastWriteTicks; e.ChangeTicks = id.ChangeTicks; e.AllocatedSize = id.AllocatedSize;
                }
                if (HideEntryIds) e.HasIdentity = false;
                handler(name, e);
            }, ct, out error);
            AfterEnumeration?.Invoke(path); return status;
        }
        public FileStatus GetIdentity(string path, out FileIdentity id) => _inner.GetIdentity(path, out id);
        public bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id) => _inner.TryGetDirectoryIdentity(path, out id);
        public SafeFileHandle OpenRead(string path, bool sequential) => _inner.OpenRead(path, sequential);
        public bool TryGetSnapshot(SafeFileHandle h, string p, out MetaSnapshot s) => _inner.TryGetSnapshot(h, p, out s);
        public StorageProfile GetStorageProfile(string path) => _inner.GetStorageProfile(path);
        public bool TryGetVolume(string path, out string root, out ulong serial)
        { root = Path.GetPathRoot(path)!; _inner.TryGetDirectoryIdentity(path, out var id); serial = id.Vol; return true; }
        public bool TryQueryJournal(string root, out ulong journal, out long next, out long lowest)
        { journal = Journal; next = Next; lowest = Lowest; return Available; }
        public bool TryReadDirectoryChanges(string root, ulong journal, long from, long to, List<UsnChange> changes, CancellationToken ct)
        { changes.AddRange(_changes.Where(c => c.Usn >= from && c.Usn < to).Select(c => c.Change)); return !FailRead; }
        public bool TryReadChanges(string root, ulong journal, long from, long to, HashSet<(ulong Lo, ulong Hi)> changes, CancellationToken ct)
        { foreach (var c in _changes.Where(c => c.Usn >= from && c.Usn < to)) changes.Add((c.Change.FileLow, c.Change.FileHigh)); return !FailRead; }
    }
}
