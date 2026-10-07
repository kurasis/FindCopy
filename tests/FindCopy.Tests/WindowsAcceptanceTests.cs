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
