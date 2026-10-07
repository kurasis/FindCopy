using FindCopy.Core;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

[SupportedOSPlatform("windows")]
static class WindowsAcceptanceTests
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("W1 native long-path verified recycling", () =>
        {
            string d = Path.Combine(root, "long-recycle");
            for (int i = 0; i < 7; i++) d = Path.Combine(d, new string((char)('a' + i), 45));
            Directory.CreateDirectory(d);
            var backend = new WindowsDeletionBackend();
            Require(backend.RecycleBinAvailable(WindowsFileSystem.ToExtendedPath(d)), "extended local path misclassified as network");
            Require(!backend.RecycleBinAvailable(@"\\?\UNC\server\share\file"), "extended UNC path misclassified as local");
            File.WriteAllText(Path.Combine(d, "a"), "long path recycling"); File.WriteAllText(Path.Combine(d, "b"), "long path recycling");
            var r = Scan(d); var g = r.Groups.Single(); var candidate = g.Files.Single(f => f.Path.EndsWith("b"));
            var result = new DuplicateDeleter().Run(new[] { new DeleteRequest(g, new[] { candidate }) }, DeleteMode.RecycleBin).Single();
            Require(result.Deleted && result.Reason == null && result.FreedBytes == 0 && !File.Exists(candidate.Path) &&
                File.Exists(Path.Combine(d, "a")), "long-path recycling: " + result.Reason);
            Require(!Directory.EnumerateDirectories(d, ".FindCopy-recycle-*").Any(), "successful stage not cleaned");
        });
        test("W2 native USN warm scan avoids unchanged directory enumeration", () =>
        {
            string d = Path.Combine(root, "native-usn"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "a"), "USN pair"); File.WriteAllText(Path.Combine(d, "b"), "USN pair");
            var fs = new WindowsFileSystem();
            if (!fs.TryGetVolume(d, out var volume, out _) || !fs.TryQueryJournal(volume, out _, out _, out _))
                throw new SkippedTestException("Native journal is unavailable on this volume/account");
            string cache = Path.Combine(root, "native-usn.db");
            Scan(d, cache, exact: false); var warm = Scan(d, cache, exact: false);
            Require(warm.Counters.InventoryDirectoriesReused == 1 && warm.Counters.FastEnumeratedDirectories == 0 &&
                warm.Counters.FallbackEnumeratedDirectories == 0 && warm.Counters.ContentBytesRead == 0, "warm native inventory not reused");
            File.AppendAllText(Path.Combine(d, "b"), "changed");
            var changed = Scan(d, cache);
            Require(changed.Groups.Count == 0 && changed.Counters.InventoryDirectoriesReused == 0, "changed native parent reused");
        });
        test("W3 native EFS logical content and access denial", () =>
        {
            string d = Path.Combine(root, "efs"); Directory.CreateDirectory(d);
            string encrypted = Path.Combine(d, "encrypted"), plain = Path.Combine(d, "plain");
            File.WriteAllText(encrypted, "authorized EFS plaintext"); File.Copy(encrypted, plain);
            try { File.Encrypt(encrypted); }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            { throw new SkippedTestException("EFS unavailable: " + ex.Message); }
            Require((File.GetAttributes(encrypted) & FileAttributes.Encrypted) != 0, "fixture not encrypted");
            Require(Scan(d).Groups.Single().Verification == VerificationState.ExactMatch, "EFS logical bytes differ");
            var file = new FileInfo(encrypted); var security = file.GetAccessControl();
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
            try
            {
                security.AddAccessRule(deny); file.SetAccessControl(security);
                var denied = Scan(d);
                Require(denied.Groups.Count == 0 && denied.IssueCounts.GetValueOrDefault(FileStatus.AccessDenied) == 1,
                    "denied EFS file should be excluded with ACCESS_DENIED");
            }
            finally { security.RemoveAccessRuleSpecific(deny); file.SetAccessControl(security); }
        });
        test("W4 native Cloud Files placeholder is never hydrated by default", () =>
        {
            string d = Path.Combine(root, "cloud-api"); Directory.CreateDirectory(d);
            using var cloud = new CloudFixture(d);
            var r = Scan(d);
            Require(r.Counters.ContentBytesRead == 0 && r.Counters.SkippedFiles == 2 &&
                r.IssueCounts.GetValueOrDefault(FileStatus.CloudContentNotLocal) == 2 && r.HasUncheckedFiles,
                "real non-local placeholders were not excluded");
        });
        test("W5 writers remain blocked while the shell recycles all aliases", () =>
        {
            string d = Path.Combine(root, "recycle-guard"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "a"), "guarded pair"); File.WriteAllText(Path.Combine(d, "b"), "guarded pair");
            Native.HardLink(Path.Combine(d, "b"), Path.Combine(d, "alias"));
            var r = Scan(d); var g = r.Groups.Single(); var candidate = g.Files.Single(f => f.HardLinkAliasCount == 1);
            var backend = new WriterProbe();
            var result = new DuplicateDeleter(backend: backend).Run(new[] { new DeleteRequest(g, new[] { candidate }) }, DeleteMode.RecycleBin).Single();
            Require(result.Deleted && result.Reason == null && backend.Blocked == 2, "recycle write guard: " + result.Reason);
        });
    }

    public static void RunRecovery(Action<string, Action> test, string root)
    {
        test("W6 long Unicode path recovery preserves identity and ADS without overwriting", () =>
        {
            string d = Path.Combine(root, "восстановление");
            for (int i = 0; i < 7; i++) d = Path.Combine(d, new string((char)('a' + i), 45));
            Directory.CreateDirectory(d);
            string original = Path.Combine(d, new string('я', 140));
            File.WriteAllText(original, "recoverable bytes"); File.WriteAllText(original + ":extra:$DATA", "alternate bytes");
            File.WriteAllText(Path.Combine(d, "keeper"), "recoverable bytes");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-long"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            var fs = new WindowsFileSystem(); fs.GetIdentity(original, out var before);
            var group = Scan(d).Groups.Single(); var file = group.Files.Single(f => f.Path == original);
            var deleted = new DuplicateDeleter(backend: backend).Run(new[] { new DeleteRequest(group, new[] { file }) }, DeleteMode.RecycleBin).Single();
            Require(deleted.Deleted && deleted.Reason == null, "recycling did not persist recovery: " + deleted.Reason);
            var entry = service.Journal.Load(out var errors).Single();
            Require(errors.Count == 0 && entry.State == RecoveryState.Recycled && File.Exists(entry.RecyclePath), "bin path missing");
            File.WriteAllText(original, "replacement must survive");
            var collision = service.Restore(entry.Id);
            Require(!collision.Restored && File.ReadAllText(original) == "replacement must survive" && File.Exists(entry.RecyclePath), "destination overwritten");
            File.Delete(original);
            var result = new WindowsRecoveryService(service.Journal.DirectoryPath).Restore(entry.Id);
            Require(result.Restored && result.Reason == null && File.ReadAllText(original) == "recoverable bytes" &&
                File.ReadAllText(original + ":extra:$DATA") == "alternate bytes", "long-path restore: " + result.Reason);
            fs.GetIdentity(original, out var after);
            Require(before.FileIdLow == after.FileIdLow && before.FileIdHigh == after.FileIdHigh && before.VolumeSerial == after.VolumeSerial,
                "restore copied content instead of restoring the same object");
            Require(service.Journal.Get(entry.Id).State == RecoveryState.Restored && !File.Exists(entry.RecyclePath) &&
                !service.Restore(entry.Id).Restored, "restore history or idempotence");
        });
        test("W7 staged recovery refuses missing parents and junction redirection", () =>
        {
            string d = Path.Combine(root, "restore-parent"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "staged bytes");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-stage"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out _, out var error), "stage: " + error);
            var entry = service.Journal.Load(out _).Single();
            Directory.Delete(d);
            Require(!service.Restore(entry.Id).Restored && File.Exists(entry.StagedPath), "missing parent restored");
            string redirected = Path.Combine(root, "redirected"); Directory.CreateDirectory(redirected); Native.Junction(redirected, d);
            Require(!service.Restore(entry.Id).Restored && !File.Exists(Path.Combine(redirected, "file")), "junction followed");
            Directory.Delete(d); Directory.CreateDirectory(d);
            var result = service.Restore(entry.Id);
            Require(result.Restored && File.ReadAllText(original) == "staged bytes" && !Directory.Exists(Path.GetDirectoryName(entry.StagedPath)),
                "stage recovery: " + result.Reason);
        });
        test("W8 recovery refuses a modified staged object", () =>
        {
            string d = Path.Combine(root, "restore-mutated"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "before");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-mutation"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out _, out var error), "stage: " + error);
            var entry = service.Journal.Load(out _).Single();
            try
            {
                File.AppendAllText(entry.StagedPath, "modified");
                Require(!service.Restore(entry.Id).Restored && !File.Exists(original) && File.ReadAllText(entry.StagedPath) == "beforemodified",
                    "changed staged file moved");
            }
            finally { Directory.Delete(Path.GetDirectoryName(entry.StagedPath)!, true); }
        });
        test("W9 recovery refuses altered bin metadata and permits a safe retry", () =>
        {
            string d = Path.Combine(root, "restore-metadata"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "metadata bytes");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-metadata"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            string staged;
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out staged, out var error), "stage: " + error);
            Require(backend.MoveToRecycleBin(staged, out var recycleError) && recycleError == null, "recycle: " + recycleError);
            var entry = service.Journal.Load(out _).Single();
            string metadata = Path.Combine(Path.GetDirectoryName(entry.RecyclePath)!, "$I" + Path.GetFileName(entry.RecyclePath)![2..]);
            byte[] bytes = File.ReadAllBytes(metadata), changed = (byte[])bytes.Clone(); changed[16] ^= 1;
            File.WriteAllBytes(metadata, changed);
            Require(!service.Restore(entry.Id).Restored && !File.Exists(original) && File.Exists(entry.RecyclePath), "changed metadata accepted");
            File.WriteAllBytes(metadata, bytes);
            var result = service.Restore(entry.Id);
            Require(result.Restored && result.Reason == null && !File.Exists(metadata), "retry or bin metadata cleanup: " + result.Reason);
        });
        test("W11 open writers block recovery until their handle closes", () =>
        {
            string d = Path.Combine(root, "restore-writer"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "writer bytes");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-writer"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out _, out var error), "stage: " + error);
            var entry = service.Journal.Load(out _).Single();
            using (var writer = File.OpenHandle(entry.StagedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Require(!service.Restore(entry.Id).Restored && File.Exists(entry.StagedPath), "writer was not excluded");
            var result = service.Restore(entry.Id);
            Require(result.Restored && result.Reason == null, "restore after writer closed: " + result.Reason);
        });
        test("W12 a same-size same-date replacement is never adopted for recovery", () =>
        {
            string d = Path.Combine(root, "restore-replacement"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "before");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-replacement"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath);
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out _, out var error), "stage: " + error);
            var entry = service.Journal.Load(out _).Single();
            try
            {
                File.Move(entry.StagedPath, entry.StagedPath + ".actual");
                File.WriteAllText(entry.StagedPath, "before");
                File.SetCreationTimeUtc(entry.StagedPath, DateTime.FromFileTimeUtc(entry.Version.CreationTicks));
                File.SetLastWriteTimeUtc(entry.StagedPath, DateTime.FromFileTimeUtc(entry.Version.LastWriteTicks));
                Require(!service.Restore(entry.Id).Restored && !File.Exists(original) && File.Exists(entry.StagedPath + ".actual"),
                    "replacement at a recorded path was restored");
            }
            finally { Directory.Delete(Path.GetDirectoryName(entry.StagedPath)!, true); }
        });
        test("W13 a shell move interrupted before journal update remains recoverable", () =>
        {
            string d = Path.Combine(root, "restore-interrupted"); Directory.CreateDirectory(d);
            string original = Path.Combine(d, "file"); File.WriteAllText(original, "interrupted bytes");
            var service = new WindowsRecoveryService(Path.Combine(root, "history-interrupted"));
            var backend = new WindowsDeletionBackend(service.Journal.DirectoryPath); string staged;
            using (var h = backend.OpenCandidate(original)) Require(backend.StageForRecycle(h, original, out staged, out var error), "stage: " + error);
            Require(backend.MoveToRecycleBin(staged, out var recycleError) && recycleError == null, "recycle: " + recycleError);
            var entry = service.Journal.Load(out _).Single();
            service.Journal.Save(entry with { State = RecoveryState.Staged, RecyclePath = null, MetadataHash = null });
            var result = new WindowsRecoveryService(service.Journal.DirectoryPath).Restore(entry.Id);
            Require(result.Restored && result.Reason == null && File.ReadAllText(original) == "interrupted bytes" &&
                service.Journal.Get(entry.Id).State == RecoveryState.Restored, "interrupted recycle recovery: " + result.Reason);
        });
        test("W10 independently recycled hard-link aliases restore their original paths", () =>
        {
            string d = Path.Combine(root, "restore-aliases"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "keeper"), "alias bytes"); File.WriteAllText(Path.Combine(d, "copy"), "alias bytes");
            Native.HardLink(Path.Combine(d, "copy"), Path.Combine(d, "alias"));
            var service = new WindowsRecoveryService(Path.Combine(root, "history-aliases"));
            var group = Scan(d).Groups.Single(); var file = group.Files.Single(f => f.HardLinkAliasCount == 1);
            var result = new DuplicateDeleter(backend: new WindowsDeletionBackend(service.Journal.DirectoryPath))
                .Run(new[] { new DeleteRequest(group, new[] { file }) }, DeleteMode.RecycleBin).Single();
            Require(result.Deleted && result.Reason == null, "alias recycling: " + result.Reason);
            var entries = service.Journal.Load(out _);
            Require(entries.Count == 2 && entries.All(e => service.Restore(e.Id).Restored), "alias restore");
            Require(Scan(d).Groups.Single().Files.Single(f => f.HardLinkAliasCount == 1).HardLinkAliasCount == 1, "hard links not preserved");
        });
    }

    private sealed class WriterProbe : IDeletionBackend
    {
        private readonly IDeletionBackend _inner = new WindowsDeletionBackend();
        public int Blocked;
        public Microsoft.Win32.SafeHandles.SafeFileHandle OpenKeeper(string path) => _inner.OpenKeeper(path);
        public Microsoft.Win32.SafeHandles.SafeFileHandle OpenCandidate(string path) => _inner.OpenCandidate(path);
        public bool DeletePermanently(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, out string? error) => _inner.DeletePermanently(handle, path, out error);
        public bool RecycleBinAvailable(string path) => _inner.RecycleBinAvailable(path);
        public bool StageForRecycle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string original, out string staged, out string? error) => _inner.StageForRecycle(handle, original, out staged, out error);
        public bool MoveToRecycleBin(string path, out string? error)
        {
            try { using var writer = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { Blocked++; }
            return _inner.MoveToRecycleBin(path, out error);
        }
    }

    private static ScanResult Scan(string root, string? cache = null, bool exact = true) => new ScanController().RunAsync(
        new ScanOptions { Roots = new[] { root }, ExactVerification = exact, CachePath = cache }).GetAwaiter().GetResult();
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }

    private sealed class CloudFixture : IDisposable
    {
        private readonly string _root;
        private bool _registered;
        public CloudFixture(string root)
        {
            _root = root;
            var registration = new Registration { StructSize = (uint)Marshal.SizeOf<Registration>(),
                ProviderName = "FindCopy acceptance fixture", ProviderVersion = "1.0", ProviderId = Guid.NewGuid() };
            var policy = new Policies { StructSize = (uint)Marshal.SizeOf<Policies>(), PopulationPrimary = 2 };
            int hr;
            try { hr = CfRegisterSyncRoot(root, ref registration, ref policy, 6); }
            catch (DllNotFoundException ex) { throw new SkippedTestException("Cloud Files API unavailable: " + ex.Message); }
            if (hr < 0) throw new SkippedTestException("Cloud Files sync root unavailable: 0x" + hr.ToString("X8"));
            _registered = true;
            IntPtr identity = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, identity, 16);
                var entries = new[]
                {
                    new Placeholder { Name = "online-a", Metadata = new Metadata { Size = 4096, Attributes = 0x80 }, Identity = identity, IdentityLength = 16, Flags = 2 },
                    new Placeholder { Name = "online-b", Metadata = new Metadata { Size = 4096, Attributes = 0x80 }, Identity = identity, IdentityLength = 16, Flags = 2 },
                };
                hr = CfCreatePlaceholders(root, entries, 2, 1, out uint processed);
                Require(hr >= 0 && processed == 2 && entries.All(e => e.Result >= 0), "native placeholder creation failed: 0x" + hr.ToString("X8"));
                foreach (var entry in entries)
                    Require(((uint)File.GetAttributes(Path.Combine(root, entry.Name)) & FileAttr.NotLocalMask) != 0, "fixture is already local");
            }
            catch { Dispose(); throw; }
            finally { Marshal.FreeHGlobal(identity); }
        }
        public void Dispose() { if (_registered) { CfUnregisterSyncRoot(_root); _registered = false; } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Registration
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProviderName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProviderVersion;
        public IntPtr RootIdentity; public uint RootIdentityLength;
        public IntPtr FileIdentity; public uint FileIdentityLength; public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Policies
    {
        public uint StructSize; public ushort HydrationPrimary, HydrationModifier, PopulationPrimary, PopulationModifier;
        public uint InSync, Hardlink, Management;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Metadata
    {
        public long Creation, Access, Write, Change; public uint Attributes; public long Size;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Placeholder
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public Metadata Metadata; public IntPtr Identity; public uint IdentityLength, Flags;
        public int Result; public long Usn;
    }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfRegisterSyncRoot(string path, ref Registration registration, ref Policies policies, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfUnregisterSyncRoot(string path);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfCreatePlaceholders(string path, [In, Out] Placeholder[] entries, uint count, uint flags, out uint processed);
}
