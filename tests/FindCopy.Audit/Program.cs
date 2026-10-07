using FindCopy.Core;
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
ScanResult Scan(string d, IFileSystem? fs=null, bool exact=false, string? cache=null, Action<string,int>? block=null) {
 var options = new ScanOptions { Roots=new[]{d}, ExactVerification=exact, CachePath=cache, UseUsnJournal=false };
 // Exercise the original internal test hook without changing production source or friend assemblies.
 if (block != null) typeof(ScanOptions).GetProperty("AfterFullHashBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(options, block);
 return new ScanController(fs).RunAsync(options).GetAwaiter().GetResult();
}
try {
 Console.WriteLine("FileRecord fixed bytes: " + Marshal.SizeOf<FileRecord>());
 Check(Marshal.SizeOf<FileRecord>() <= 128, "R5: fixed metadata meets the approximate 128-byte target");
 var d=Pair("exact-growth"); int sequential=0;
 var fs=new HookFs { OnOpen=(p,s)=>{ if(s && ++sequential==3) { using(var a=File.Open(Path.Combine(d,"a"),FileMode.Append)) a.WriteByte(1); using(var b=File.Open(Path.Combine(d,"b"),FileMode.Append)) b.WriteByte(2); } } };
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
} finally { Directory.Delete(root,true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class HookFs : IFileSystem {
 readonly IFileSystem inner=FileSystemBase.CreateDefault();
 public Action<string,bool>? OnOpen;
 public Action<string>? AfterIdentity;
 public Func<string,uint>? ExtraAttributes;
 public bool NoSnapshot;
 public string NormalizeRoot(string p)=>inner.NormalizeRoot(p);
 public FileStatus EnumerateDirectory(string d,DirEntryHandler h,CancellationToken ct,out string? e)=>inner.EnumerateDirectory(d,(ReadOnlySpan<char> n,in EntryInfo x)=>{ var y=x; y.Attributes|=ExtraAttributes?.Invoke(n.ToString())??0; h(n,y); },ct,out e);
 public FileStatus GetIdentity(string p,out FileIdentity i) { var s=inner.GetIdentity(p,out i); AfterIdentity?.Invoke(p); return s; }
 public bool TryGetDirectoryIdentity(string p,out (ulong Vol,ulong Lo,ulong Hi) i)=>inner.TryGetDirectoryIdentity(p,out i);
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
