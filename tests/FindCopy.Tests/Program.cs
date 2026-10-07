using System.Runtime.InteropServices;
using System.Text;
using FindCopy.Core;
using Microsoft.Win32.SafeHandles;

// Correctness tests from ТЗ §27. Runs on Linux (PortableFileSystem) and on Windows (WindowsFileSystem).
int passed = 0, failed = 0, skipped = 0;
Console.WriteLine($"RUNTIME: OS={RuntimeInformation.OSDescription}; OS architecture={RuntimeInformation.OSArchitecture}; process={RuntimeInformation.ProcessArchitecture}");
string? expectedArchitecture = Environment.GetEnvironmentVariable("FINDCOPY_EXPECTED_ARCH");
if (!string.IsNullOrEmpty(expectedArchitecture) && expectedArchitecture != RuntimeInformation.ProcessArchitecture.ToString())
    throw new InvalidOperationException("Unexpected test process architecture: " + RuntimeInformation.ProcessArchitecture);
var root = Path.Combine(Path.GetTempPath(), "findcopy-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);

void Check(bool cond, string what)
{
    if (!cond) throw new Exception("FAILED: " + what);
}

string NewDir(string name)
{
    var d = Path.Combine(root, name);
    Directory.CreateDirectory(d);
    return d;
}

byte[] Rand(int size, int seed)
{
    var b = new byte[size];
    new Random(seed).NextBytes(b);
    return b;
}

ScanResult Scan(string dir, Action<ScanOptionsBuilder>? cfg = null, IFileSystem? fs = null, IQuickHasher? q = null, IFullHasher? f = null, CancellationToken ct = default)
{
    var b = new ScanOptionsBuilder { Roots = new[] { dir } };
    cfg?.Invoke(b);
    return new ScanController(fs, q, f).RunAsync(b.Build(), ct).GetAwaiter().GetResult();
}

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("  ok   " + name); }
    catch (SkippedTestException ex) { skipped++; Console.WriteLine("  SKIP " + name + ": " + ex.Message); }
    catch (Exception ex) { failed++; Console.WriteLine("  FAIL " + name + ": " + ex.Message); }
}

const int MiB = 1 << 20;
bool canStat = OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);

Console.WriteLine("FindCopy engine tests, root: " + root);
if (args.Contains("--network-only"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SMB acceptance requires Windows");
    try { NetworkAcceptanceTests.Run(Test, root, Environment.GetEnvironmentVariable("FINDCOPY_SMB_ROOT") ?? throw new InvalidOperationException("Missing FINDCOPY_SMB_ROOT")); }
    finally { Directory.Delete(root, recursive: true); }
    Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped");
    return failed == 0 ? 0 : 1;
}

Test("01 same bytes, different names/extensions/timestamps -> duplicate", () =>
{
    var d = NewDir("t01");
    var data = Rand(5000, 1);
    File.WriteAllBytes(Path.Combine(d, "photo.jpg"), data);
    File.WriteAllBytes(Path.Combine(d, "copy.bin"), data);
    File.SetLastWriteTimeUtc(Path.Combine(d, "copy.bin"), new DateTime(2001, 1, 1));
    File.WriteAllBytes(Path.Combine(d, "other.bin"), Rand(5000, 2));
    var r = Scan(d);
    Check(r.Groups.Count == 1, "one group");
    Check(r.Groups[0].Files.Count == 2, "two files");
    Check(r.Groups[0].ReclaimableLogicalBytes == 5000, "reclaimable");
    Check(r.Groups[0].Verification == VerificationState.HashMatch, "hash match");
});

Test("02 same size, different first bytes -> rejected at Q1, no full hash", () =>
{
    var d = NewDir("t02");
    for (int i = 0; i < 5; i++) File.WriteAllBytes(Path.Combine(d, $"f{i}"), Rand(3 * MiB, 100 + i));
    var r = Scan(d);
    Check(r.Groups.Count == 0, "no groups");
    Check(r.Counters.FullHashFiles == 0, "no full hashing");
    Check(r.Counters.QuickHashBytesRead == 5 * 64 * 1024, "only Q1 read: " + r.Counters.QuickHashBytesRead);
});

Test("03 same size and start, different end -> rejected at Q2", () =>
{
    var d = NewDir("t03");
    var a = Rand(3 * MiB, 3); var b = (byte[])a.Clone(); b[^1] ^= 0xFF;
    File.WriteAllBytes(Path.Combine(d, "a"), a);
    File.WriteAllBytes(Path.Combine(d, "b"), b);
    var r = Scan(d);
    Check(r.Groups.Count == 0, "no groups");
    Check(r.Counters.FullHashFiles == 0, "no full hashing");
    Check(r.Counters.QuickHashBytesRead == 4 * 64 * 1024, "Q1+Q2 read");
});

Test("03b large files, same header/footer, different middle -> rejected at Q3", () =>
{
    var d = NewDir("t03b");
    var a = Rand(20 * MiB, 33); var b = (byte[])a.Clone();
    long mid = ((20L * MiB - 65536) / 2) & ~4095L;
    b[mid + 10] ^= 0x01;
    File.WriteAllBytes(Path.Combine(d, "a"), a);
    File.WriteAllBytes(Path.Combine(d, "b"), b);
    var r = Scan(d);
    Check(r.Groups.Count == 0, "no groups");
    Check(r.Counters.FullHashFiles == 0, "no full hashing");
});

Test("04 identical samples, one byte differs outside samples -> full hash detects", () =>
{
    var d = NewDir("t04");
    var a = Rand(20 * MiB, 4); var b = (byte[])a.Clone();
    b[5 * MiB + 123] ^= 0x01;
    File.WriteAllBytes(Path.Combine(d, "a"), a);
    File.WriteAllBytes(Path.Combine(d, "b"), b);
    File.WriteAllBytes(Path.Combine(d, "c"), a);
    var r = Scan(d);
    Check(r.Counters.FullHashFiles == 3, "full hashed 3");
    Check(r.Groups.Count == 1 && r.Groups[0].Files.Count == 2, "only a and c are duplicates");
    Check(r.Groups[0].Files.All(f => !f.Path.EndsWith("b")), "b not in group");
});

Test("05 forced hash collision -> ExactVerifier separates files", () =>
{
    var d = NewDir("t05");
    var a = Rand(3 * MiB, 5);
    File.WriteAllBytes(Path.Combine(d, "a"), a);
    File.WriteAllBytes(Path.Combine(d, "a2"), a);
    File.WriteAllBytes(Path.Combine(d, "b"), Rand(3 * MiB, 6));
    File.WriteAllBytes(Path.Combine(d, "c"), Rand(3 * MiB, 7));
    var weak = Scan(d, q: new ConstQuick(), f: new ConstFull());
    Check(weak.Groups.Count == 1 && weak.Groups[0].Files.Count == 4, "mock collision makes one HASH_MATCH group");
    var strict = Scan(d, o => o.ExactVerification = true, q: new ConstQuick(), f: new ConstFull());
    Check(strict.Groups.Count == 1, "exact verifier keeps only real duplicates");
    Check(strict.Groups[0].Files.Count == 2 && strict.Groups[0].Verification == VerificationState.ExactMatch, "a + a2 exact match");
});

if (canStat)
{
    Test("06 two hard links of one object -> not a duplicate", () =>
    {
        var d = NewDir("t06");
        File.WriteAllBytes(Path.Combine(d, "x"), Rand(4000, 8));
        Native.HardLink(Path.Combine(d, "x"), Path.Combine(d, "x-link"));
        var r = Scan(d);
        Check(r.Groups.Count == 0, "no groups");
        Check(r.Counters.HardlinkAliasesDetected == 1, "alias detected");
        Check(r.Counters.FullHashFiles == 0, "no content read");
    });

    Test("07 hard link + real copy -> one redundant physical copy", () =>
    {
        var d = NewDir("t07");
        var data = Rand(4000, 9);
        File.WriteAllBytes(Path.Combine(d, "x"), data);
        Native.HardLink(Path.Combine(d, "x"), Path.Combine(d, "x-link"));
        File.WriteAllBytes(Path.Combine(d, "copy"), data);
        var r = Scan(d);
        Check(r.Groups.Count == 1, "one group");
        var g = r.Groups[0];
        Check(g.UniquePhysicalFileCount == 2 && g.FileCount == 3, $"2 physical, 3 paths ({g.UniquePhysicalFileCount}/{g.FileCount})");
        Check(g.ReclaimableLogicalBytes == 4000, "reclaimable = one copy");
        Check(g.Files.Sum(f => f.HardLinkAliasCount) == 1, "alias listed");
    });
}

{
    Test("08 directory link loop -> no infinite recursion", () =>
    {
        var d = NewDir("t08");
        var sub = Path.Combine(d, "sub"); Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "f"), Rand(100, 10));
        if (OperatingSystem.IsWindows()) Native.Junction(d, Path.Combine(sub, "loop"));
        else Directory.CreateSymbolicLink(Path.Combine(sub, "loop"), d);
        var r1 = Scan(d);
        Check(r1.IssueCounts.GetValueOrDefault(FileStatus.ReparseSkipped) == 1, "link skipped by default");
        var r2 = Scan(d, o => o.FollowDirectoryReparsePoints = true);
        Check(r2.Counters.DirectoriesScanned == 2, "visited set stops loop: " + r2.Counters.DirectoriesScanned);
    });
}

Test("09 symlink to a file inside the tree -> not a second copy", () =>
{
    var d = NewDir("t09");
    File.WriteAllBytes(Path.Combine(d, "target"), Rand(1000, 11));
    try { File.CreateSymbolicLink(Path.Combine(d, "link"), Path.Combine(d, "target")); }
    catch (Exception ex) { throw new SkippedTestException("Cannot create symlink: " + ex.Message); }
    var r = Scan(d);
    Check(r.Groups.Count == 0, "no groups");
    Check(r.Counters.FilesDiscovered == 1, "symlink not counted");
});

Test("10 file modified during full hash -> CHANGED_DURING_SCAN", () =>
{
    var d = NewDir("t10");
    var data = Rand(3 * MiB, 12);
    File.WriteAllBytes(Path.Combine(d, "a"), data);
    File.WriteAllBytes(Path.Combine(d, "b"), data);
    var victim = Path.Combine(d, "b");
    var r = Scan(d, o => o.AfterFullHashBlock = (p, block) =>
    {
        if (p == victim && block == 0)
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(10, SeekOrigin.Begin); fs.WriteByte(0x42); fs.Flush();
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
        }
    });
    Check(r.Groups.Count == 0, "no duplicate reported");
    Check(r.IssueCounts.GetValueOrDefault(FileStatus.ChangedDuringScan) == 1, "changed status");
    Check(r.Counters.ChangedFiles == 1, "counted");
});

Test("11 file deleted during scan -> status, scan continues", () =>
{
    var d = NewDir("t11");
    var data = Rand(2000, 13);
    for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(d, $"f{i}"), data);
    bool done = false;
    var r = Scan(d, o => o.AfterFullHashBlock = (p, _) =>
    {
        if (done) return;
        done = true;
        foreach (var other in Directory.GetFiles(d).Where(x => x != p).Take(1)) File.Delete(other);
    });
    Check(r.Groups.Count == 1, "remaining duplicates reported");
    Check(r.IssueCounts.GetValueOrDefault(FileStatus.FileNotFoundDuringScan) == 1, "not-found status");
    Check(r.Groups[0].Files.Count >= 2, "group");
});

Test("12 access denied -> scan continues", () =>
{
    var d = NewDir("t12");
    var data = Rand(2000, 14);
    for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(d, $"f{i}"), data);
    var denied = Path.Combine(d, "f0");
    var fs = new FakeFs(FileSystemBase.CreateDefault()) { Deny = p => p == denied };
    var r = Scan(d, fs: fs);
    Check(r.IssueCounts.GetValueOrDefault(FileStatus.AccessDenied) == 1, "access denied recorded");
    Check(r.Groups.Count == 1 && r.Groups[0].Files.Count == 2, "other two still duplicates");
    Check(r.HasUncheckedFiles, "result is qualified");
});

Test("13 sparse and regular file with same logical content -> duplicate", () =>
{
    var d = NewDir("t13");
    var tail = Rand(1000, 15);
    using (var fs = new FileStream(Path.Combine(d, "sparse"), FileMode.Create))
    {
        if (OperatingSystem.IsWindows()) Native.Sparse(fs.SafeFileHandle);
        fs.SetLength(8 * MiB);
        fs.Seek(8 * MiB - 1000, SeekOrigin.Begin);
        fs.Write(tail);
    }
    var full = new byte[8 * MiB];
    tail.CopyTo(full, 8 * MiB - 1000);
    File.WriteAllBytes(Path.Combine(d, "regular"), full);
    var r = Scan(d);
    Check(r.Groups.Count == 1, "duplicate");
});

if (OperatingSystem.IsWindows())
{
    Test("14 NTFS compressed and uncompressed logical-equivalent files", () =>
    {
        var d = NewDir("t14");
        var bytes = new byte[4 * MiB]; bytes[100] = 42;
        File.WriteAllBytes(Path.Combine(d, "compressed"), bytes);
        File.WriteAllBytes(Path.Combine(d, "regular"), bytes);
        using (var handle = File.OpenHandle(Path.Combine(d, "compressed"), FileMode.Open, FileAccess.ReadWrite)) Native.Compress(handle);
        Check((File.GetAttributes(Path.Combine(d, "compressed")) & FileAttributes.Compressed) != 0, "compressed attribute");
        Check(Scan(d).Groups.Single().UniquePhysicalFileCount == 2, "logical contents match");
    });
}
else { skipped++; Console.WriteLine("  SKIP 14 native NTFS compression requires Windows"); }

Test("15 zero-byte files -> separate list, reclaimable 0", () =>
{
    var d = NewDir("t15");
    for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(d, $"e{i}"), Array.Empty<byte>());
    var r = Scan(d);
    Check(r.ZeroByteFiles.Count == 3, "zero list");
    Check(r.Groups.Count == 0 && r.TotalReclaimableLogical == 0, "no reclaim");
    Check(r.Counters.ContentBytesRead == 0, "nothing read");
});

Test("16 Unicode paths", () =>
{
    var d = NewDir("t16 Юникод 日本語 🙂");
    var data = Rand(3000, 16);
    File.WriteAllBytes(Path.Combine(d, "файл.txt"), data);
    File.WriteAllBytes(Path.Combine(d, "ファイル.txt"), data);
    var r = Scan(d);
    Check(r.Groups.Count == 1, "duplicate found");
    Check(r.Groups[0].Files.Any(f => f.Path.Contains("файл.txt")), "path kept");
});

Test("17 paths longer than 260 chars", () =>
{
    var d = NewDir("t17");
    var deep = d;
    for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string((char)('a' + i), 50));
    Directory.CreateDirectory(deep);
    var data = Rand(3000, 17);
    var p1 = Path.Combine(deep, "one.bin");
    File.WriteAllBytes(p1, data);
    File.WriteAllBytes(Path.Combine(d, "two.bin"), data);
    Check(p1.Length > 400, "long path");
    var r = Scan(d);
    Check(r.Groups.Count == 1, "duplicate across long path");
});

Test("18 many files with unique sizes -> zero content I/O", () =>
{
    var d = NewDir("t18");
    for (int i = 1; i <= 3000; i++) File.WriteAllBytes(Path.Combine(d, $"u{i}"), new byte[i]);
    var r = Scan(d);
    Check(r.Counters.FilesDiscovered == 3000, "discovered");
    Check(r.Counters.UniqueSizeFilesRejected == 3000, "all rejected by size");
    Check(r.Counters.ContentBytesRead == 0 && r.Counters.ReadAmplification == 0, "no content read");
});

Test("19 online-only cloud file -> not read by default", () =>
{
    var d = NewDir("t19");
    var data = Rand(3000, 19);
    File.WriteAllBytes(Path.Combine(d, "local"), data);
    File.WriteAllBytes(Path.Combine(d, "cloud"), data);
    var fs = new FakeFs(FileSystemBase.CreateDefault()) { ExtraAttributes = n => n == "cloud" ? FileAttr.RecallOnDataAccess : 0 };
    var r = Scan(d, fs: fs);
    Check(r.Groups.Count == 0, "cloud file not compared");
    Check(r.IssueCounts.GetValueOrDefault(FileStatus.CloudContentNotLocal) == 1, "status");
    Check(fs.Opened.Count == 0, "cloud file never opened");
    var r2 = Scan(d, o => o.IncludeOnlineOnlyFiles = true, fs: fs);
    Check(r2.Groups.Count == 1, "included when allowed");
});

Test("20 cancel during large file read -> clean stop", () =>
{
    var d = NewDir("t20");
    var data = Rand(48 * MiB, 20);
    File.WriteAllBytes(Path.Combine(d, "big1"), data);
    File.WriteAllBytes(Path.Combine(d, "big2"), data);
    using var cts = new CancellationTokenSource();
    int blocks = 0;
    var t0 = DateTime.UtcNow;
    try
    {
        Scan(d, o => o.AfterFullHashBlock = (_, _) => { if (Interlocked.Increment(ref blocks) == 3) cts.Cancel(); }, ct: cts.Token);
        throw new Exception("no cancellation");
    }
    catch (OperationCanceledException) { }
    Check(blocks < 10, "stopped promptly: " + blocks);
});

Test("21 non-recursive mode ignores subfolders", () =>
{
    var d = NewDir("t21");
    var data = Rand(3000, 21);
    File.WriteAllBytes(Path.Combine(d, "a"), data);
    Directory.CreateDirectory(Path.Combine(d, "sub"));
    File.WriteAllBytes(Path.Combine(d, "sub", "b"), data);
    Check(Scan(d, o => o.Recursive = false).Groups.Count == 0, "non-recursive");
    Check(Scan(d, o => o.Recursive = true).Groups.Count == 1, "recursive");
});

Test("22 system files are skipped", () =>
{
    var d = NewDir("t22");
    var data = Rand(3000, 22);
    File.WriteAllBytes(Path.Combine(d, "a"), data);
    File.WriteAllBytes(Path.Combine(d, "sys"), data);
    var fs = new FakeFs(FileSystemBase.CreateDefault()) { ExtraAttributes = n => n == "sys" ? FileAttr.System : 0 };
    var r = Scan(d, fs: fs);
    Check(r.Groups.Count == 0 && r.Counters.SystemSkipped == 1, "system skipped");
});

Test("23 overlapping roots are not scanned twice", () =>
{
    var d = NewDir("t23");
    Directory.CreateDirectory(Path.Combine(d, "sub"));
    File.WriteAllBytes(Path.Combine(d, "sub", "only"), Rand(3000, 23));
    var b = new ScanOptionsBuilder { Roots = new[] { d, Path.Combine(d, "sub") } };
    var r = new ScanController().RunAsync(b.Build()).GetAwaiter().GetResult();
    Check(r.Groups.Count == 0 && r.Counters.FilesDiscovered == 1, "single scan");
});

Test("24 excluded (system) directories are not descended into", () =>
{
    var d = NewDir("t24");
    var data = Rand(3000, 24);
    File.WriteAllBytes(Path.Combine(d, "a"), data);
    Directory.CreateDirectory(Path.Combine(d, "Windows"));
    File.WriteAllBytes(Path.Combine(d, "Windows", "b"), data);
    var b = new ScanOptionsBuilder { Roots = new[] { d } };
    var opts = b.Build();
    var r = new ScanController().RunAsync(new ScanOptions { Roots = opts.Roots, ExcludedDirectories = new[] { Path.Combine(d, "Windows") } }).GetAwaiter().GetResult();
    Check(r.Groups.Count == 0 && r.Counters.FilesDiscovered == 1, "excluded dir skipped");
});

Test("25 cache: repeat scan reads no content, same result", () =>
{
    var d = NewDir("t25");
    var cache = Path.Combine(root, "cache25.db");
    var small = Rand(4000, 25); var big = Rand(5 * MiB, 26);
    File.WriteAllBytes(Path.Combine(d, "s1"), small); File.WriteAllBytes(Path.Combine(d, "s2"), small);
    File.WriteAllBytes(Path.Combine(d, "b1"), big); File.WriteAllBytes(Path.Combine(d, "b2"), big);
    File.WriteAllBytes(Path.Combine(d, "b3"), Rand(5 * MiB, 27));
    var r1 = Scan(d, o => o.CachePath = cache);
    Check(r1.Groups.Count == 2 && r1.Counters.CacheHits == 0 && r1.Counters.CacheWrites > 0, "first scan fills cache");
    var r2 = Scan(d, o => o.CachePath = cache);
    Check(r2.Groups.Count == 2, "same groups");
    Check(r2.Counters.CacheHits == 5, "all candidates hit: " + r2.Counters.CacheHits);
    Check(r2.Counters.FullHashBytesRead == 0, "no full reads: " + r2.Counters.FullHashBytesRead);
    Check(r2.Counters.ContentBytesRead == 0, "no content read at all: " + r2.Counters.ContentBytesRead);
    Check(r2.Groups.Select(g => g.Hash).OrderBy(x => x).SequenceEqual(r1.Groups.Select(g => g.Hash).OrderBy(x => x)), "same hashes");
});

Test("26 cache: changed file is re-hashed, never a false duplicate", () =>
{
    var d = NewDir("t26");
    var cache = Path.Combine(root, "cache26.db");
    var data = Rand(4000, 28);
    File.WriteAllBytes(Path.Combine(d, "a"), data); File.WriteAllBytes(Path.Combine(d, "b"), data);
    Check(Scan(d, o => o.CachePath = cache).Groups.Count == 1, "dup at first");
    var p = Path.Combine(d, "b");
    var mtime = File.GetLastWriteTimeUtc(p);
    var changed = (byte[])data.Clone(); changed[100] ^= 1;
    File.WriteAllBytes(p, changed);
    File.SetLastWriteTimeUtc(p, mtime);   // same size and mtime: only ctime / USN can tell
    var r = Scan(d, o => o.CachePath = cache);
    Check(r.Groups.Count == 0, "no stale duplicate");
    Check(r.Counters.CacheHits == 1 && r.Counters.CacheMisses == 1, $"one hit, one miss ({r.Counters.CacheHits}/{r.Counters.CacheMisses})");
});

Test("27 cache: damaged cache file is rebuilt", () =>
{
    var d = NewDir("t27");
    var cache = Path.Combine(root, "cache27.db");
    File.WriteAllText(cache, "this is not a sqlite database at all, just garbage garbage garbage");
    var data = Rand(4000, 29);
    File.WriteAllBytes(Path.Combine(d, "a"), data); File.WriteAllBytes(Path.Combine(d, "b"), data);
    var r = Scan(d, o => o.CachePath = cache);
    Check(r.Groups.Count == 1, "scan works");
    Check(Scan(d, o => o.CachePath = cache).Counters.CacheHits == 2, "cache usable after rebuild");
});

Test("28 cache: cancelled scan leaves a consistent cache", () =>
{
    var d = NewDir("t28");
    var cache = Path.Combine(root, "cache28.db");
    var data = Rand(24 * MiB, 30);
    File.WriteAllBytes(Path.Combine(d, "a"), data); File.WriteAllBytes(Path.Combine(d, "b"), data);
    using var cts = new CancellationTokenSource();
    int blocks = 0;
    try { Scan(d, o => { o.CachePath = cache; o.AfterFullHashBlock = (_, _) => { if (++blocks == 5) cts.Cancel(); }; }, ct: cts.Token); }
    catch (OperationCanceledException) { }
    var r = Scan(d, o => o.CachePath = cache);
    Check(r.Groups.Count == 1, "correct after cancel");
    Check(r.Counters.FullHashFiles == 2, "partial hashes were not stored");
    Check(Scan(d, o => o.CachePath = cache).Counters.FullHashBytesRead == 0, "complete hashes stored afterwards");
});

if (canStat)
{
    Test("29 USN journal: reported change invalidates cache entry; journal reset drops volume", () =>
    {
        var d = NewDir("t29");
        var cache = Path.Combine(root, "cache29.db");
        var data = Rand(4000, 31);
        File.WriteAllBytes(Path.Combine(d, "a"), data); File.WriteAllBytes(Path.Combine(d, "b"), data);
        var fs = new FakeUsnFs(FileSystemBase.CreateDefault());
        Scan(d, o => o.CachePath = cache, fs: fs);             // starts tracking at USN 100
        fs.Next = 150;
        fs.GetIdentity(Path.Combine(d, "b"), out var idB);
        fs.Changes.Add((idB.FileIdLow, idB.FileIdHigh));
        var r = Scan(d, o => o.CachePath = cache, fs: fs);
        Check(r.Counters.UsnVolumesTracked == 1 && r.Counters.UsnInvalidated == 1, $"journal applied ({r.Counters.UsnVolumesTracked}/{r.Counters.UsnInvalidated})");
        Check(r.Counters.CacheHits == 1 && r.Counters.CacheMisses == 1, "changed file re-hashed");
        Check(fs.LastFrom == 100 && fs.LastTo == 150, "read from saved position");
        fs.Changes.Clear();
        fs.Journal = 2;                                       // journal re-created: nothing can be trusted
        var r3 = Scan(d, o => o.CachePath = cache, fs: fs);
        Check(r3.Counters.CacheHits == 0 && r3.Groups.Count == 1, "volume invalidated, still correct");
    });
}

Test("30 NVMe autotune path: concurrent full reads stay correct", () =>
{
    var d = NewDir("t30");
    var data = Rand(40 * MiB, 32);
    for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(d, $"n{i}"), data);
    File.WriteAllBytes(Path.Combine(d, "other"), Rand(40 * MiB, 33));
    var fs = new FakeFs(FileSystemBase.CreateDefault()) { Kind = StorageKind.Nvme };
    var r = Scan(d, fs: fs);
    Check(r.Groups.Count == 1 && r.Groups[0].Files.Count == 4, "4 copies");
    Check(r.Storage.All(x => x.Kind == StorageKind.Nvme), "nvme domain");
});

Test("31 alternate streams mode: named streams must match too", () =>
{
    var d = NewDir("t31");
    var data = Rand(4000, 34);
    foreach (var n in new[] { "a", "b", "c" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var streamDir = NewDir("t31-streams");
    File.WriteAllBytes(Path.Combine(streamDir, "zone1"), Encoding.ASCII.GetBytes("[ZoneTransfer]\nZoneId=3"));
    File.WriteAllBytes(Path.Combine(streamDir, "zone2"), Encoding.ASCII.GetBytes("[ZoneTransfer]\nZoneId=3"));
    var fs = new FakeFs(FileSystemBase.CreateDefault());
    fs.Streams[Path.Combine(d, "a")] = new() { (":Zone.Identifier:$DATA", Path.Combine(streamDir, "zone1")) };
    fs.Streams[Path.Combine(d, "b")] = new() { (":Zone.Identifier:$DATA", Path.Combine(streamDir, "zone2")) };
    var off = Scan(d, fs: fs);
    Check(off.Groups.Count == 1 && off.Groups[0].Files.Count == 3, "default: main stream only");
    var on = Scan(d, o => o.CompareAlternateStreams = true, fs: fs);
    Check(on.Groups.Count == 1 && on.Groups[0].Files.Count == 2, "with streams: a+b only");
    Check(on.Groups[0].Files.All(f => !f.Path.EndsWith("c")), "c (no stream) separated");
    Check(on.Counters.AlternateStreamFiles == 3, "all checked");
    File.WriteAllBytes(Path.Combine(streamDir, "zone2"), Encoding.ASCII.GetBytes("[ZoneTransfer]\nZoneId=4"));
    Check(Scan(d, o => o.CompareAlternateStreams = true, fs: fs).Groups.Count == 0, "different stream content");
    Check(Scan(d, o => { o.CompareAlternateStreams = true; o.ExactVerification = true; }, fs: fs, f: new ConstFull()).Groups.Count == 0,
        "strict ADS verification rejects mocked full-hash collisions");
});

Test("32 cache: changed sample size ignores cached quick hashes, keeps full hashes", () =>
{
    var d = NewDir("t32");
    var cache = Path.Combine(root, "cache32.db");
    var big = Rand(5 * MiB, 35);
    File.WriteAllBytes(Path.Combine(d, "a"), big); File.WriteAllBytes(Path.Combine(d, "b"), big);
    var a = (byte[])big.Clone(); a[3 * MiB] ^= 1;
    File.WriteAllBytes(Path.Combine(d, "c"), a);
    new ScanController().RunAsync(new ScanOptions { Roots = new[] { d }, CachePath = cache }).GetAwaiter().GetResult();
    var r = new ScanController().RunAsync(new ScanOptions { Roots = new[] { d }, CachePath = cache, Tuning = new Tuning { SampleSize = 16 * 1024 } }).GetAwaiter().GetResult();
    Check(r.Groups.Count == 1 && r.Groups[0].Files.Count == 2, "correct with new sample size");
    Check(r.Counters.QuickHashBytesRead > 0, "quick hashes recomputed");
    Check(r.Counters.FullHashBytesRead == 0, "full hashes reused");
});

if (OperatingSystem.IsWindows())
{
    Test("33 enumeration backends agree (FileIdExtdDirectoryInfo vs FindFirstFileExW)", () =>
    {
        var d = NewDir("t33");
        var data = Rand(4000, 36);
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        File.WriteAllBytes(Path.Combine(d, "a"), data);
        File.WriteAllBytes(Path.Combine(d, "sub", "b"), data);
        Native.HardLink(Path.Combine(d, "a"), Path.Combine(d, "sub", "a-link"));
        File.WriteAllBytes(Path.Combine(d, "other"), Rand(4000, 37));
        ScanResult Run(EnumerationBackend b) => new ScanController().RunAsync(new ScanOptions { Roots = new[] { d }, EnumerationBackend = b }).GetAwaiter().GetResult();
        var fast = Run(EnumerationBackend.Auto);
        var slow = Run(EnumerationBackend.Win32Only);
        Console.WriteLine($"       (fast-path directories: {fast.Counters.FastEnumeratedDirectories}, fallback: {fast.Counters.FallbackEnumeratedDirectories})");
        Check(slow.Counters.FastEnumeratedDirectories == 0, "forced fallback");
        Check(fast.Counters.FilesDiscovered == slow.Counters.FilesDiscovered, "same files");
        foreach (var r in new[] { fast, slow })
        {
            Check(r.Groups.Count == 1 && r.Groups[0].UniquePhysicalFileCount == 2 && r.Groups[0].FileCount == 3, "same groups and hard-link handling");
        }
    });
}

else { skipped++; Console.WriteLine("  SKIP 33 native Windows enumeration backends require Windows"); }

// ---------------------------------------------------------------- deletion

List<DeleteOutcome> Delete(ScanResult r, Func<DuplicateFile, bool> pick, DeleteMode mode = DeleteMode.Permanent, IFileSystem? fs = null) =>
    new DuplicateDeleter(fs).Run(r.Groups.Select(g => new DeleteRequest(g, g.Files.Where(pick).ToList())).ToList(), mode);

Test("D1 delete selected copies, keep one", () =>
{
    var d = NewDir("d1");
    var data = Rand(3 * MiB, 40);
    foreach (var n in new[] { "a", "b", "c" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var r = Scan(d);
    var res = Delete(r, f => !f.Path.EndsWith("a"));
    Check(res.Count == 2 && res.All(o => o.Deleted), string.Join("; ", res.Select(o => o.Reason)));
    Check(File.Exists(Path.Combine(d, "a")) && !File.Exists(Path.Combine(d, "b")) && !File.Exists(Path.Combine(d, "c")), "files");
    Check(res.Sum(o => o.FreedBytes) >= 2 * 3 * MiB, "freed");
});

Test("D2 never delete every copy of a group", () =>
{
    var d = NewDir("d2");
    var data = Rand(4000, 41);
    foreach (var n in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var res = Delete(Scan(d), _ => true);
    Check(res.All(o => !o.Deleted), "refused");
    Check(File.Exists(Path.Combine(d, "a")) && File.Exists(Path.Combine(d, "b")), "both kept");
});

Test("D3 file changed after the scan is not deleted", () =>
{
    var d = NewDir("d3");
    var data = Rand(4000, 42);
    foreach (var n in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var r = Scan(d);
    var p = Path.Combine(d, "b");
    var mt = File.GetLastWriteTimeUtc(p);
    var changed = (byte[])data.Clone(); changed[10] ^= 1;
    File.WriteAllBytes(p, changed);
    File.SetLastWriteTimeUtc(p, mt);
    var res = Delete(r, f => f.Path == p);
    Check(res.Count == 1 && !res[0].Deleted, "refused: " + res[0].Reason);
    Check(File.Exists(p), "kept");
});

Test("D4 kept copy gone after the scan -> nothing deleted", () =>
{
    var d = NewDir("d4");
    var data = Rand(4000, 43);
    foreach (var n in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var r = Scan(d);
    File.Delete(Path.Combine(d, "a"));
    var res = Delete(r, f => f.Path.EndsWith("b"));
    Check(!res[0].Deleted && File.Exists(Path.Combine(d, "b")), "last copy kept: " + res[0].Reason);
});

Test("D5 Recycle Bin unavailable -> nothing deleted silently", () =>
{
    var d = NewDir("d5");
    var data = Rand(4000, 44);
    foreach (var n in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(d, n), data);
    var r = Scan(d);
    var deleter = new DuplicateDeleter(backend: new PortableDeletionBackend());
    var res = deleter.Run(r.Groups.Select(g => new DeleteRequest(g, g.Files.Where(f => f.Path.EndsWith("b")).ToList())).ToList(), DeleteMode.RecycleBin);
    Check(!res[0].Deleted && File.Exists(Path.Combine(d, "b")), "kept: " + res[0].Reason);
});

if (canStat)
{
    Test("D6 deleting a copy also removes its hard links", () =>
    {
        var d = NewDir("d6");
        var data = Rand(4000, 45);
        File.WriteAllBytes(Path.Combine(d, "x"), data);
        Native.HardLink(Path.Combine(d, "x"), Path.Combine(d, "x-link"));
        File.WriteAllBytes(Path.Combine(d, "copy"), data);
        var r = Scan(d);
        var res = Delete(r, f => f.HardLinkAliasCount > 0);
        Check(res.Count == 1 && res[0].Deleted && res[0].Reason == null, "deleted: " + res[0].Reason);
        Check(!File.Exists(Path.Combine(d, "x")) && !File.Exists(Path.Combine(d, "x-link")) && File.Exists(Path.Combine(d, "copy")), "x and its link gone, copy kept");
    });

    Test("D7 same file under two paths is never deleted as its own duplicate", () =>
    {
        var d = NewDir("d7");
        File.WriteAllBytes(Path.Combine(d, "f"), Rand(4000, 46));
        var other = Path.Combine(root, "d7-hardlink");
        Native.HardLink(Path.Combine(d, "f"), other);
        // A group as a scanner without file ids would build it (e.g. Z:\ and \\server\share).
        var g = new DuplicateGroup
        {
            LogicalSize = 4000, Hash = "x", Verification = VerificationState.HashMatch,
            Files = new[] { new DuplicateFile { Path = Path.Combine(d, "f"), LogicalSize = 4000, AllocatedSize = -1, LastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(d, "f")) },
                            new DuplicateFile { Path = other, LogicalSize = 4000, AllocatedSize = -1, LastWriteUtc = File.GetLastWriteTimeUtc(other) } },
        };
        var res = new DuplicateDeleter().Run(new[] { new DeleteRequest(g, new[] { g.Files[1] }) }, DeleteMode.Permanent);
        Check(!res[0].Deleted && File.Exists(other), "refused: " + res[0].Reason);
        if (OperatingSystem.IsWindows())
        {
            // Without file ids the open keeper handle (no delete sharing) must still block it.
            var noIds = new FakeFs(FileSystemBase.CreateDefault()) { HideIdentity = true };
            var res2 = new DuplicateDeleter(noIds).Run(new[] { new DeleteRequest(g, new[] { g.Files[1] }) }, DeleteMode.Permanent);
            Check(!res2[0].Deleted && File.Exists(other), "refused by share mode: " + res2[0].Reason);
        }
    });
}

if (OperatingSystem.IsWindows())
{
    Test("D8 Recycle Bin mode on a fixed drive", () =>
    {
        var d = NewDir("d8");
        var data = Rand(4000, 47);
        foreach (var n in new[] { "a", "b" }) File.WriteAllBytes(Path.Combine(d, n), data);
        var r = Scan(d);
        var res = Delete(r, f => f.Path.EndsWith("b"), DeleteMode.RecycleBin);
        Check(res[0].Deleted && res[0].Reason == null, "recycled: " + res[0].Reason);
        Check(File.Exists(Path.Combine(d, "a")) && !File.Exists(Path.Combine(d, "b")), "files");
    });
}

else { skipped++; Console.WriteLine("  SKIP D8 native Recycle Bin requires Windows"); }

IncrementalTests.Run(Test, root);
RecoveryTests.Run(Test, root);
if (OperatingSystem.IsWindows())
{
    WindowsAcceptanceTests.Run(Test, root);
    WindowsAcceptanceTests.RunRecovery(Test, root);
}
else { skipped += 20; Console.WriteLine("  SKIP W1-W20 native filesystem and recovery checks require Windows"); }
try { Directory.Delete(root, true); } catch { }
Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped");
return failed == 0 ? 0 : 1;

sealed class ScanOptionsBuilder
{
    public string[] Roots = Array.Empty<string>();
    public bool Recursive = true, ExactVerification, FollowDirectoryReparsePoints, IncludeOnlineOnlyFiles, CompareAlternateStreams;
    public string? CachePath;
    public Action<string, int>? AfterFullHashBlock;
    public ScanOptions Build() => new()
    {
        Roots = Roots, Recursive = Recursive, ExactVerification = ExactVerification,
        FollowDirectoryReparsePoints = FollowDirectoryReparsePoints, IncludeOnlineOnlyFiles = IncludeOnlineOnlyFiles,
        AfterFullHashBlock = AfterFullHashBlock, CachePath = CachePath, CompareAlternateStreams = CompareAlternateStreams,
    };
}

sealed class ConstQuick : IQuickHasher { public ulong Hash(ReadOnlySpan<byte> d) => 42; }
sealed class ConstFull : IFullHasher
{
    public IFullHashState Create() => new S();
    sealed class S : IFullHashState
    {
        public void Update(ReadOnlySpan<byte> d) { }
        public void Finalize(Span<byte> h) => h.Fill(7);
        public void Dispose() { }
    }
}

/// <summary>Wraps a real file system to inject attributes and access errors.</summary>
sealed class FakeFs : IFileSystem
{
    private readonly IFileSystem _inner;
    public Func<string, bool> Deny = _ => false;
    public Func<string, uint> ExtraAttributes = _ => 0;
    public List<string> Opened = new();
    public StorageKind? Kind;
    public Dictionary<string, List<(string Name, string Backing)>> Streams = new();
    public FileStatus ListAlternateStreams(string path, List<(string Name, long Size)> streams)
    {
        if (Streams.TryGetValue(path, out var list))
            foreach (var (n, b) in list) streams.Add((n, new FileInfo(b).Length));
        return FileStatus.Ok;
    }
    public SafeFileHandle OpenAlternateStream(string path, string streamName) =>
        _inner.OpenRead(Streams[path].First(x => x.Name == streamName).Backing, true);
    public FakeFs(IFileSystem inner) => _inner = inner;
    public string NormalizeRoot(string root) => _inner.NormalizeRoot(root);
    public FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error) =>
        _inner.EnumerateDirectory(dir, (ReadOnlySpan<char> name, in EntryInfo e) =>
        {
            var copy = e;
            copy.Attributes |= ExtraAttributes(name.ToString());
            handler(name, copy);
        }, ct, out error);
    public bool HideIdentity;
    public FileStatus GetIdentity(string path, out FileIdentity id)
    {
        var st = _inner.GetIdentity(path, out id);
        if (HideIdentity) id.Valid = false;
        return st;
    }
    public bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id) => _inner.TryGetDirectoryIdentity(path, out id);
    public SafeFileHandle OpenRead(string path, bool sequential)
    {
        if (Deny(path)) throw new UnauthorizedAccessException("denied");
        lock (Opened) if (ExtraAttributes(Path.GetFileName(path)) != 0) Opened.Add(path);
        return _inner.OpenRead(path, sequential);
    }
    public bool TryGetSnapshot(SafeFileHandle h, string path, out MetaSnapshot s) => _inner.TryGetSnapshot(h, path, out s);
    public StorageProfile GetStorageProfile(string path)
    {
        var p = _inner.GetStorageProfile(path);
        return Kind is { } k ? p with { Kind = k, Description = k.ToString() } : p;
    }
}

/// <summary>Scripted USN journal on top of the real file system.</summary>
sealed class FakeUsnFs : IFileSystem, IUsnSource
{
    private readonly IFileSystem _inner;
    public ulong Journal = 1;
    public long Next = 100;
    public long LastFrom, LastTo;
    public List<(ulong, ulong)> Changes = new();
    public FakeUsnFs(IFileSystem inner) => _inner = inner;
    public string NormalizeRoot(string root) => _inner.NormalizeRoot(root);
    public FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error) => _inner.EnumerateDirectory(dir, handler, ct, out error);
    public FileStatus GetIdentity(string path, out FileIdentity id) => _inner.GetIdentity(path, out id);
    public bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id) => _inner.TryGetDirectoryIdentity(path, out id);
    public SafeFileHandle OpenRead(string path, bool sequential) => _inner.OpenRead(path, sequential);
    public bool TryGetSnapshot(SafeFileHandle h, string path, out MetaSnapshot s) => _inner.TryGetSnapshot(h, path, out s);
    public StorageProfile GetStorageProfile(string path) => _inner.GetStorageProfile(path);
    public bool TryGetVolume(string path, out string volumeRoot, out ulong volumeSerial)
    {
        volumeRoot = "/";
        bool ok = _inner.TryGetDirectoryIdentity(path, out var id);
        volumeSerial = id.Vol;
        return ok;
    }
    public bool TryQueryJournal(string volumeRoot, out ulong journalId, out long nextUsn, out long lowestValidUsn)
    {
        journalId = Journal; nextUsn = Next; lowestValidUsn = 0;
        return true;
    }
    public bool TryReadChanges(string volumeRoot, ulong journalId, long fromUsn, long toUsn, HashSet<(ulong Lo, ulong Hi)> changed, CancellationToken ct)
    {
        LastFrom = fromUsn; LastTo = toUsn;
        foreach (var c in Changes) changed.Add(c);
        return true;
    }
}

sealed class SkippedTestException(string message) : Exception(message);

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    private static extern bool CompressionIoControl(SafeFileHandle h, uint code, ref ushort input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    public static void Sparse(SafeFileHandle handle)
    {
        if (!DeviceIoControl(handle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new SkippedTestException("Filesystem does not support sparse files");
    }
    public static void Compress(SafeFileHandle handle)
    {
        ushort format = 2;
        if (!CompressionIoControl(handle, 0x0009C040, ref format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new SkippedTestException("Filesystem does not support NTFS compression");
    }
    public static void Junction(string target, string link)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"";
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create junction: " + process.StandardError.ReadToEnd());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr sa);

    public static void HardLink(string existing, string newPath)
    {
        bool ok = OperatingSystem.IsWindows() ? CreateHardLinkW(newPath, existing, IntPtr.Zero) : link(existing, newPath) == 0;
        if (!ok) throw new IOException("hard link failed: " + Marshal.GetLastWin32Error());
    }
}
