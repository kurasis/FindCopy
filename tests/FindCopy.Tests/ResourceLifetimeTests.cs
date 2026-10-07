using FindCopy.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

static class ResourceLifetimeTests
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("L1 failed keeper snapshots close their handle and permit another keeper", () =>
        {
            string directory = Path.Combine(root, "keeper-snapshot-failure");
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "a", "b", "c" })
                File.WriteAllText(Path.Combine(directory, name), "identical content");
            var group = new ScanController().RunAsync(new ScanOptions { Roots = new[] { directory } })
                .GetAwaiter().GetResult().Groups.Single();
            var victim = group.Files[2];
            var fs = new FailingSnapshotFs(group.Files[0].Path);
            try
            {
                var outcome = new DuplicateDeleter(fs).Run(new[] { new DeleteRequest(group, new[] { victim }) }, DeleteMode.Permanent).Single();
                Require(fs.FailedHandle is { IsClosed: true }, "failed keeper handle leaked");
                Require(outcome.Deleted && !File.Exists(victim.Path) && group.Files.Take(2).All(f => File.Exists(f.Path)),
                    "fallback keeper or preserved copies changed");
            }
            finally { fs.FailedHandle?.Dispose(); }
        });

        test("L2 failed cache schema initialization closes the database without deleting it", () =>
        {
            string path = Path.Combine(root, "invalid-cache-schema.db");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY);";
                command.ExecuteNonQuery();
            }
            try
            {
                using var unexpected = new ScanCache(path);
                throw new Exception("malformed schema was accepted");
            }
            catch (SqliteException ex) { Require(ex.SqliteErrorCode == 1, "unexpected initialization failure"); }
            Require(File.Exists(path), "a non-corruption failure deleted the cache");
            if (OperatingSystem.IsLinux())
            {
                foreach (string descriptor in Directory.EnumerateFiles("/proc/self/fd"))
                {
                    string? target;
                    try { target = new FileInfo(descriptor).LinkTarget; }
                    catch (FileNotFoundException) { continue; }
                    Require(target != path && target != path + "-wal" && target != path + "-shm", "failed cache left an open descriptor");
                }
            }
            else
            {
                // Windows refuses this rename while SQLite's native handle is open.
                File.Move(path, path + ".moved");
                File.Move(path + ".moved", path);
            }
        });

        test("L3 scheduler configuration failures wait for started workers to finish", () =>
        {
            var domains = new StorageDomains();
            domains.GetOrAdd(new StorageProfile("first", StorageKind.Hdd, "first"));
            domains.GetOrAdd(new StorageProfile("second", StorageKind.Ssd, "second"));
            var scheduler = new IoScheduler(domains, 1);
            using var started = new ManualResetEventSlim();
            using var failed = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var injected = new IOException("injected domain configuration failure");
            var run = Task.Run(() =>
            {
                try
                {
                    scheduler.Run(new[] { 0, 1 }, item => item, kind =>
                    {
                        if (kind == StorageKind.Hdd) return 1;
                        Require(started.Wait(TimeSpan.FromSeconds(5)), "first worker did not start");
                        failed.Set();
                        throw injected;
                    }, 1024, false, (_, _, _) =>
                    {
                        started.Set();
                        Require(release.Wait(TimeSpan.FromSeconds(5)), "worker was not released");
                    }, default);
                    return null;
                }
                catch (Exception ex) { return ex; }
            });
            bool returnedEarly;
            try
            {
                Require(failed.Wait(TimeSpan.FromSeconds(5)), "failure was not injected");
                returnedEarly = run.Wait(TimeSpan.FromMilliseconds(150));
            }
            finally { release.Set(); }
            Require(ReferenceEquals(run.GetAwaiter().GetResult(), injected), "original configuration exception was lost");
            Require(!returnedEarly, "Run returned while its worker was still active");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FailingSnapshotFs(string failingPath) : FileSystemBase
    {
        private readonly IFileSystem _inner = FileSystemBase.CreateDefault();
        public SafeFileHandle? FailedHandle { get; private set; }
        public override FileStatus EnumerateDirectory(string path, DirEntryHandler handler, CancellationToken ct, out string? error) =>
            _inner.EnumerateDirectory(path, handler, ct, out error);
        public override FileStatus GetIdentity(string path, out FileIdentity identity) => _inner.GetIdentity(path, out identity);
        public override bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id) => _inner.TryGetDirectoryIdentity(path, out id);
        public override StorageProfile GetStorageProfile(string path) => _inner.GetStorageProfile(path);
        public override bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot snapshot)
        {
            if (path == failingPath)
            {
                FailedHandle = handle;
                throw new IOException("injected keeper metadata failure");
            }
            return _inner.TryGetSnapshot(handle, path, out snapshot);
        }
    }
}
