using FindCopy.Core;

static class NetworkAcceptanceTests
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Run(Action<string, Action> test, string cacheHome, string share)
    {
        if (!OperatingSystem.IsWindows() || !share.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Network acceptance requires an actual Windows UNC share");
        string root = Path.Combine(share, "FindCopy-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ScanResult Scan(string path, string? cache = null, bool exact = false, Action<string, int>? block = null, CancellationToken ct = default) =>
            new ScanController().RunAsync(new ScanOptions { Roots = new[] { path }, CachePath = cache,
                ExactVerification = exact, AfterFullHashBlock = block }, ct).GetAwaiter().GetResult();
        string Dir(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
        try
        {
            test("N1 SMB Unicode long paths and exact content groups", () =>
            {
                string d = Dir("юникод");
                for (int i = 0; i < 7; i++) { d = Path.Combine(d, new string((char)('a' + i), 40)); Directory.CreateDirectory(d); }
                string a = Path.Combine(d, "оригинал.txt"), b = Path.Combine(d, "копия.bin");
                File.WriteAllText(a, "network bytes"); File.WriteAllText(b, "network bytes");
                File.SetLastWriteTimeUtc(b, new DateTime(2001, 1, 1));
                File.WriteAllText(Path.Combine(d, "other"), "different data");
                var r = Scan(d, exact: true);
                Require(a.Length > 260 && !r.HasUncheckedFiles && r.Groups.Count == 1 &&
                    r.Groups[0].Verification == VerificationState.ExactMatch &&
                    r.Groups[0].Files.Select(f => f.Path).ToHashSet().SetEquals(new[] { a, b }), "UNC long-path membership or exact verification");
                Require(new WindowsFileSystem().GetStorageProfile(d).Kind == StorageKind.Network, "UNC storage domain was not classified as network");
            });
            test("N2 SMB hard links do not inflate physical copies or savings", () =>
            {
                string d = Dir("links"), original = Path.Combine(d, "original");
                File.WriteAllText(original, "linked network bytes"); Native.HardLink(original, Path.Combine(d, "alias"));
                File.Copy(original, Path.Combine(d, "copy"));
                var r = Scan(d, exact: true); var g = r.Groups.Single();
                Require(!r.HasUncheckedFiles && g.UniquePhysicalFileCount == 2 && g.Files.Count == 2 &&
                    g.Files.Sum(f => f.HardLinkAliasCount) == 1 && g.ReclaimableLogicalBytes == new FileInfo(original).Length,
                    "SMB aliases became independent reclaimable copies");
            });
            test("N3 SMB cache reuse and changed content work without USN inventory", () =>
            {
                string d = Dir("cache"), cache = Path.Combine(cacheHome, "network-cache.db");
                byte[] bytes = new byte[2 << 20]; new Random(32).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(d, "a"), bytes); File.WriteAllBytes(Path.Combine(d, "b"), bytes);
                Require(Scan(d, cache).Groups.Count == 1, "cache seed");
                var warm = Scan(d, cache);
                Require(!warm.HasUncheckedFiles && warm.Groups.Count == 1 && warm.Counters.ContentBytesRead == 0 &&
                    warm.Counters.InventoryDirectoriesReused == 0 &&
                    warm.Counters.FastEnumeratedDirectories + warm.Counters.FallbackEnumeratedDirectories > 0,
                    "network cache must validate live enumeration without a local journal");
                bytes[900000] ^= 1; string changed = Path.Combine(d, "b"); File.WriteAllBytes(changed, bytes);
                File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
                var updated = Scan(d, cache);
                Require(!updated.HasUncheckedFiles && updated.Groups.Count == 0, "SMB stale cache produced a false duplicate");
            });
            test("N4 SMB streaming cancellation preserves a usable cache", () =>
            {
                string d = Dir("cancel"), cache = Path.Combine(cacheHome, "network-cancel.db");
                byte[] block = new byte[1 << 20]; new Random(42).NextBytes(block);
                foreach (string name in new[] { "a", "b" })
                {
                    using var stream = File.Create(Path.Combine(d, name));
                    for (int i = 0; i < 64; i++) stream.Write(block);
                }
                using var cts = new CancellationTokenSource(); bool cancelled = false;
                try { Scan(d, cache, block: (_, _) => cts.Cancel(), ct: cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "SMB full hashing was not cancelled");
                var resumed = Scan(d, cache, exact: true);
                Require(!resumed.HasUncheckedFiles && resumed.Groups.Single().Verification == VerificationState.ExactMatch &&
                    resumed.Counters.FullHashBytesRead > 0, "partial SMB full hash was trusted or cache became unusable");
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
