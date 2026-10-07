using FindCopy.Core;
using FindCopy.Bench;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Reflection;

int passed = 0, failed = 0;
void Check(bool condition, string requirement) {
 if (condition) { passed++; Console.WriteLine("  PASS " + requirement); }
 else { failed++; Console.WriteLine("  FAIL " + requirement); }
}

string root = Path.Combine(Path.GetTempPath(), "findcopy-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Pair(string name, int size = 4096) {
 string d=Path.Combine(root,name); Directory.CreateDirectory(d);
 byte[] data=new byte[size]; new Random(42).NextBytes(data);
 File.WriteAllBytes(Path.Combine(d,"a"),data); File.WriteAllBytes(Path.Combine(d,"b"),data);
 return d;
}
ScanResult Scan(string d, IFileSystem? fs=null, bool exact=false, string? cache=null, Action<string,int>? block=null, CancellationToken token=default) {
 var options = new ScanOptions { Roots=new[]{d}, ExactVerification=exact, CachePath=cache, UseUsnJournal=false };
 // Exercise the original internal test hook without changing production source or friend assemblies.
 if (block != null) typeof(ScanOptions).GetProperty("AfterFullHashBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(options, block);
 return new ScanController(fs).RunAsync(options, token).GetAwaiter().GetResult();
}
try {
 Console.WriteLine("FileRecord fixed bytes: " + Marshal.SizeOf<FileRecord>());
 Check(Marshal.SizeOf<FileRecord>() <= 128, "R5: fixed metadata meets the approximate 128-byte target");
 var d=Pair("exact-growth"); int sequential=0;
 var fs=new HookFs { OnOpen=(p,s)=>{ if(s && Interlocked.Increment(ref sequential)==3) { using(var a=File.Open(Path.Combine(d,"a"),FileMode.Append)) a.WriteByte(1); using(var b=File.Open(Path.Combine(d,"b"),FileMode.Append)) b.WriteByte(2); } } };
 var r=Scan(d,fs,exact:true);
 Console.WriteLine($"EXACT_GROWTH: groups={r.Groups.Count}, verification={r.Groups.FirstOrDefault()?.Verification}, currentBytesEqual={File.ReadAllBytes(Path.Combine(d,"a")).SequenceEqual(File.ReadAllBytes(Path.Combine(d,"b")))}, changed={r.Counters.ChangedFiles}");
 Check(r.Groups.Count == 0, "R1: appended unequal tails must not receive EXACT_MATCH");
 d=Pair("cache-race"); string cache=Path.Combine(root,"cache.db"); Scan(d,cache:cache);
 bool modified=false;
 fs=new HookFs { AfterIdentity=p=>{ if(p==Path.Combine(d,"b") && !modified) { modified=true; var bytes=File.ReadAllBytes(p); bytes[0]^=255; File.WriteAllBytes(p,bytes); } } };
 r=Scan(d,fs,cache:cache);
 Console.WriteLine($"CACHE_RACE: groups={r.Groups.Count}, hits={r.Counters.CacheHits}, contentReads={r.Counters.ContentBytesRead}, currentBytesEqual={File.ReadAllBytes(Path.Combine(d,"a")).SequenceEqual(File.ReadAllBytes(Path.Combine(d,"b")))}, changed={r.Counters.ChangedFiles}");
 Check(r.Groups.Count == 0, "R2: a changed cached candidate must not remain a duplicate");
 d=Pair("snapshot-unavailable"); bool changed=false;
 fs=new HookFs { NoSnapshot=true };
 r=Scan(d,fs,block:(p,b)=>{ if(p==Path.Combine(d,"b") && !changed) { changed=true; var bytes=File.ReadAllBytes(p); bytes[0]^=255; File.WriteAllBytes(p,bytes); } });
 Console.WriteLine($"NO_SNAPSHOT: groups={r.Groups.Count}, currentBytesEqual={File.ReadAllBytes(Path.Combine(d,"a")).SequenceEqual(File.ReadAllBytes(Path.Combine(d,"b")))}, changed={r.Counters.ChangedFiles}");
 Check(r.IssueCounts.GetValueOrDefault(FileStatus.Unsupported) == 2 && r.Counters.ContentBytesRead == 0, "R3: unavailable snapshots are reported before content I/O");
 Check(r.Groups.Count == 0, "R3: unavailable snapshots must not allow a modified file to match");
 d=Pair("system-skip",100);
 fs=new HookFs { ExtraAttributes=p=>p=="b" ? FileAttr.System:0 };
 r=Scan(d,fs);
 Console.WriteLine($"SKIP_QUALIFIER: skipped={r.Counters.SkippedFiles}, issues={r.IssueCounts.Count}, hasUnchecked={r.HasUncheckedFiles}");
 Check(r.HasUncheckedFiles, "R4: skipped files require a qualified completeness result");

 d=Pair("external-alias"); string external=Path.Combine(root,"outside-alias");
 Native.HardLink(Path.Combine(d,"b"),external);
 r=Scan(d);
 var g=r.Groups.Single(); var victim=g.Files.Single(f=>f.Path==Path.Combine(d,"b"));
 var deletion=new DuplicateDeleter().Run(new[]{new DeleteRequest(g,new[]{victim})},DeleteMode.Permanent).Single();
 Console.WriteLine($"EXTERNAL_ALIAS: links={victim.LinkCount}, knownAliases={victim.HardLinkAliasCount}, outsideExists={File.Exists(external)}, reportedFreed={deletion.FreedBytes}");
 Check(deletion.FreedBytes == 0, "R6: an outside hard link means no physical space was freed");
 d=Pair("alias-replacement"); string alias=Path.Combine(d,"c-alias");
 Native.HardLink(Path.Combine(d,"b"),alias);
 r=Scan(d); g=r.Groups.Single(); victim=g.Files.Single(f=>f.HardLinkAliasCount==1);
 string aliasToReplace=victim.HardLinkAliases.Single(); bool replaced=false;
 fs=new HookFs { AfterIdentity=p=>{ if(p==aliasToReplace && !replaced) { replaced=true; File.Delete(p); File.WriteAllText(p,"unrelated replacement must survive"); } } };
 deletion=new DuplicateDeleter(fs).Run(new[]{new DeleteRequest(g,new[]{victim})},DeleteMode.Permanent).Single();
 Console.WriteLine($"ALIAS_REPLACEMENT: replaced={replaced}, unrelatedReplacementSurvives={File.Exists(aliasToReplace)}, outcomeDeleted={deletion.Deleted}");
 Check(File.Exists(aliasToReplace), "R7: a replacement at an alias path must survive deletion");

 // Optimized enumeration can fail after emitting a prefix. Retry must neither lose nor repeat entries.
 var seen = new List<string>();
 FileStatus Fast(DirEntryHandler emit, out string? error) { error = "injected late error"; emit("a", new EntryInfo()); return FileStatus.IoError; }
 FileStatus Slow(DirEntryHandler emit, out string? error) { error = null; emit("a", new EntryInfo()); emit("b", new EntryInfo()); return FileStatus.Ok; }
 var status = DirectoryEnumerationFallback.Run(Fast, Slow, (ReadOnlySpan<char> n, in EntryInfo _) => seen.Add(n.ToString()), default, out bool fallback, out _);
 Check(status == FileStatus.Ok && fallback && seen.SequenceEqual(new[] { "a", "b" }), "late enumeration fallback emits each file once");

 // Refuse following directories when their identity cannot be obtained.
 d = Pair("directory-identity");
 fs = new HookFs { NoDirectoryIdentity = true };
 var options = new ScanOptions { Roots = new[] { d }, FollowDirectoryReparsePoints = true };
 r = new ScanController(fs).RunAsync(options).GetAwaiter().GetResult();
 Check(r.Counters.DirectoriesScanned == 0 && r.HasUncheckedFiles && r.IssueCounts.ContainsKey(FileStatus.Unsupported), "follow mode fails safely without directory identity");

 // Exercise the >=1 GiB staged path without allocating GiB arrays or fully hashing the fixture.
 d = Path.Combine(root, "quarter-samples"); Directory.CreateDirectory(d);
 const long hugeSize = (1L << 30) + 1048576;
 foreach (string name in new[] { "a", "b", "c" }) DatasetGenerator.CreateSparseFile(Path.Combine(d, name), hugeSize);
 long quarter = ((hugeSize - 65536) / 4) & ~4095L;
 long threeQuarter = ((hugeSize - 65536) / 4 * 3) & ~4095L;
 using (var file = new FileStream(Path.Combine(d, "a"), FileMode.Open, FileAccess.Write)) { file.Position = quarter; file.WriteByte(1); }
 using (var file = new FileStream(Path.Combine(d, "b"), FileMode.Open, FileAccess.Write)) { file.Position = threeQuarter; file.WriteByte(2); }
 r = Scan(d);
 Check(r.Groups.Count == 0 && r.Counters.FullHashFiles == 0 && r.Counters.QuickHashBytesRead == 14L * 65536, "Q4/Q5 run only for surviving large candidates");

 d = Path.Combine(root, "cancel-101gib"); Directory.CreateDirectory(d);
 foreach (string name in new[] { "a", "b" }) DatasetGenerator.CreateSparseFile(Path.Combine(d, name), 101L << 30);
 using var cancellation = new CancellationTokenSource();
 bool cancelled = false;
 try { Scan(d, cache: Path.Combine(root, "huge-cache.db"), block: (_, _) => cancellation.Cancel(), token: cancellation.Token); }
 catch (OperationCanceledException) { cancelled = true; }
 using var retainedCache = new ScanCache(Path.Combine(root, "huge-cache.db"));
 bool noPartialFull = true;
 foreach (var path in Directory.GetFiles(d))
 {
     FileSystemBase.CreateDefault().GetIdentity(path, out var id);
     var key = new CacheKey { HasId = id.Valid, VolumeSerial = id.VolumeSerial, FileIdLow = id.FileIdLow,
         FileIdHigh = id.FileIdHigh, Path = path, Size = id.Size, LastWriteTicks = id.LastWriteTicks,
         ChangeTicks = id.ChangeTicks, CreationTicks = id.CreationTicks };
     noPartialFull &= retainedCache.TryGet(key, out var entry) && entry!.FullHash == null;
 }
 Check(cancelled && noPartialFull && retainedCache.EntryCount == 2, "101 GiB cancellation preserves completed quick cache entries only");

 // Regression for the generator: a long size is streamed with a bounded caller-owned buffer.
 string patternPath = Path.Combine(root, "pattern.bin");
 DatasetGenerator.WritePatternFile(patternPath, 3L * 1048576 + 17, new byte[] { 1, 2, 3 });
 Check(new FileInfo(patternPath).Length == 3L * 1048576 + 17, "streamed generator preserves non-aligned lengths");
 Check(Scan(Path.Combine(root, "exact-growth"), exact: true).Groups.Count == 0, "stable unequal tails remain unequal on a subsequent scan");

 // The original name may be reused after handle-bound staging; only the staged copy is recycled.
 d = Pair("recycle-replacement"); r = Scan(d); g = r.Groups.Single();
 victim = g.Files.Single(f => f.Path == Path.Combine(d, "b"));
 var recycle = new RecycleProbe();
 deletion = new DuplicateDeleter(backend: recycle).Run(new[] { new DeleteRequest(g, new[] { victim }) }, DeleteMode.RecycleBin).Single();
 Check(deletion.Deleted && deletion.FreedBytes == 0 && recycle.Staged &&
     File.ReadAllText(victim.Path) == "replacement at original path", "recycling uses the verified staged object and preserves replacements");

 d = Pair("keeper-changed"); r = Scan(d); g = r.Groups.Single();
 victim = g.Files.Single(f => f.Path == Path.Combine(d, "b"));
 File.SetLastWriteTimeUtc(Path.Combine(d, "a"), DateTime.UtcNow.AddDays(-1));
 deletion = new DuplicateDeleter().Run(new[] { new DeleteRequest(g, new[] { victim }) }, DeleteMode.Permanent).Single();
 Check(!deletion.Deleted && File.Exists(victim.Path), "a keeper changed since scan cannot authorize deletion");

 d = Pair("empty-aliases", 0);
 Native.HardLink(Path.Combine(d, "a"), Path.Combine(d, "a-link"));
 r = Scan(d);
 Check(r.ZeroByteGroups.Single().UniquePhysicalFileCount == 2 && r.ZeroByteGroups.Single().FileCount == 3 &&
     r.ZeroByteGroups.Single().ReclaimableLogicalBytes == 0 && r.Counters.ContentBytesRead == 0, "empty duplicates have physical groups without content I/O");
} finally { Directory.Delete(root,true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class HookFs : IFileSystem {
 readonly IFileSystem inner=FileSystemBase.CreateDefault();
 public Action<string,bool>? OnOpen;
 public Action<string>? AfterIdentity;
 public Func<string,uint>? ExtraAttributes;
 public bool NoSnapshot;
 public bool NoDirectoryIdentity;
 public string NormalizeRoot(string p)=>inner.NormalizeRoot(p);
 public FileStatus EnumerateDirectory(string d,DirEntryHandler h,CancellationToken ct,out string? e)=>inner.EnumerateDirectory(d,(ReadOnlySpan<char> n,in EntryInfo x)=>{ var y=x; y.Attributes|=ExtraAttributes?.Invoke(n.ToString())??0; h(n,y); },ct,out e);
 public FileStatus GetIdentity(string p,out FileIdentity i) { var s=inner.GetIdentity(p,out i); AfterIdentity?.Invoke(p); return s; }
 public bool TryGetDirectoryIdentity(string p,out (ulong Vol,ulong Lo,ulong Hi) i){ if(NoDirectoryIdentity){ i=default; return false; } return inner.TryGetDirectoryIdentity(p,out i); }
 public SafeFileHandle OpenRead(string p,bool s) { OnOpen?.Invoke(p,s); return inner.OpenRead(p,s); }
 public bool TryGetSnapshot(SafeFileHandle h,string p,out MetaSnapshot s) { if(NoSnapshot){s=default;return false;} return inner.TryGetSnapshot(h,p,out s); }
 public StorageProfile GetStorageProfile(string p)=>inner.GetStorageProfile(p);
}

static class Native {
 [DllImport("libc",SetLastError=true)] static extern int link(string a,string b);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateHardLinkW(string a,string b,IntPtr c);
 public static void HardLink(string source,string target) {
  bool ok=OperatingSystem.IsWindows()?CreateHardLinkW(target,source,IntPtr.Zero):link(source,target)==0;
  if(!ok) throw new IOException("Hard link failed: "+Marshal.GetLastWin32Error());
 }
}

sealed class RecycleProbe : IDeletionBackend
{
    private readonly IDeletionBackend inner = DuplicateDeleter.CreateDefaultBackend();
    public bool Staged;
    public SafeFileHandle OpenKeeper(string path) => inner.OpenKeeper(path);
    public SafeFileHandle OpenCandidate(string path) => inner.OpenCandidate(path);
    public bool DeletePermanently(SafeFileHandle handle, string path, out string? error) => inner.DeletePermanently(handle, path, out error);
    public bool RecycleBinAvailable(string path) => true;
    public bool StageForRecycle(SafeFileHandle handle, string original, out string staged, out string? error)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!inner.StageForRecycle(handle, original, out staged, out error)) return false;
        }
        else { staged = original + ".private-staged"; File.Move(original, staged); error = null; }
        Staged = true;
        File.WriteAllText(original, "replacement at original path");
        return true;
    }
    public bool MoveToRecycleBin(string path, out string? error)
    {
        if (!Staged || File.ReadAllText(path) == "replacement at original path") throw new IOException("Unverified recycle path");
        File.Delete(path); error = null;
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "original-path.txt"));
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
        return true;
    }
}
