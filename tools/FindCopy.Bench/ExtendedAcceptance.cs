using FindCopy.Core;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FindCopy.Bench;

/// <summary>Executable dense-file and actual NTFS-tree acceptance, with correctness gates.</summary>
public static class ExtendedAcceptance
{
    private static string _csv = "";
    public static int Execute(string[] args)
    {
        try
        {
            if (args.Length is < 3 or > 4 || args[2] is not ("dense" or "native" or "workload"))
                throw new ArgumentException("Usage: <work-dir> --extended-acceptance dense|native|workload [size-MiB|unique-file-count]");
            string work = Path.GetFullPath(args[0]);
            string owner = Path.Combine(work, "extended-acceptance-owned.txt");
            if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any() &&
                (!File.Exists(owner) || File.ReadAllText(owner) != "FindCopy extended acceptance v1"))
                throw new IOException("Refusing an unrecognized acceptance workspace");
            Directory.CreateDirectory(work); File.WriteAllText(owner, "FindCopy extended acceptance v1");
            _csv = Path.Combine(work, "extended-acceptance.csv");
            if (!File.Exists(_csv)) File.WriteAllText(_csv,
                "utc;mode;phase;expected_files;files;logical_bytes;elapsed_s;peak_scan_ram_mib;content_bytes;quick_bytes;full_bytes;exact_bytes;native_directories;inventory_directories;groups;errors;changed;complete;cancelled;os;cpu_count;memory_budget_mib\n");
            int amount = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : args[2] == "dense" ? 10240 : args[2] == "workload" ? 100_000 : 5_000_000;
            if (args[2] == "dense")
            {
                if (amount < 16 || amount > 102400) throw new ArgumentOutOfRangeException(nameof(amount));
                Dense(work, amount * 1048576L);
            }
            else if (args[2] == "workload")
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mixed workload acceptance requires Windows/NTFS");
                if (amount < 20_000 || amount > 1_000_000) throw new ArgumentOutOfRangeException(nameof(amount));
                Workload(work, amount);
            }
            else
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real-tree suite requires native Windows/NTFS");
                if (amount < 20_000 || amount > 10_000_000) throw new ArgumentOutOfRangeException(nameof(amount));
                NativeTree(work, amount);
            }
            Console.WriteLine("PASS extended acceptance. Results: " + _csv);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL extended acceptance: " + ex); return 1; }
    }

    private static ScanResult Measure(string mode, string phase, string root, string? cache, long expected,
        bool exact = false, bool cancel = false, bool cancelFull = false)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); long peak = process.WorkingSet64;
        using var sampler = new Timer(_ =>
        {
            using var p = Process.GetCurrentProcess(); long size = p.WorkingSet64, old;
            do { old = Interlocked.Read(ref peak); if (size <= old) break; }
            while (Interlocked.CompareExchange(ref peak, size, old) != old);
        }, null, 0, 10);
        var controller = new ScanController(); using var cts = new CancellationTokenSource();
        var options = new ScanOptions { Roots = new[] { root }, CachePath = cache, ExactVerification = exact };
        if (cancelFull)
            typeof(ScanOptions).GetProperty("AfterFullHashBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(options, (Action<string, int>)((_, _) => cts.Cancel()));
        var watch = Stopwatch.StartNew(); var task = controller.RunAsync(options, cts.Token);
        Task monitor = cancel ? Task.Run(async () =>
        {
            while (!task.IsCompleted)
            {
                if (Interlocked.Read(ref controller.Counters.FilesDiscovered) >= 10_000) { cts.Cancel(); return; }
                await Task.Delay(1);
            }
        }) : Task.CompletedTask;
        ScanResult? result = null; bool cancelled = false;
        try { result = task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
        finally { monitor.GetAwaiter().GetResult(); watch.Stop(); sampler.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var c = result?.Counters ?? controller.Counters.Snapshot(); process.Refresh();
        double ram = Math.Max(Interlocked.Read(ref peak), process.WorkingSet64) / 1048576.0;
        object[] row = { DateTime.UtcNow.ToString("O"), mode, phase, expected, c.FilesDiscovered, c.LogicalBytesDiscovered,
            watch.Elapsed.TotalSeconds, ram, c.ContentBytesRead, c.QuickHashBytesRead, c.FullHashBytesRead,
            c.ExactCompareBytesRead, c.FastEnumeratedDirectories + c.FallbackEnumeratedDirectories,
            c.InventoryDirectoriesReused, result?.Groups.Count ?? 0, c.ErrorFiles, c.ChangedFiles,
            result != null && !result.HasUncheckedFiles, cancelled, RuntimeInformation.OSDescription,
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576 };
        File.AppendAllText(_csv, string.Join(";", row.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))) + "\n");
        Console.WriteLine($"{mode} {phase}: files={c.FilesDiscovered}, native={c.FastEnumeratedDirectories + c.FallbackEnumeratedDirectories}, reused={c.InventoryDirectoriesReused}, bytes={c.ContentBytesRead}, RAM={ram:F2} MiB, time={watch.Elapsed.TotalSeconds:F3}s, cancelled={cancelled}");
        if (cancel || cancelFull)
        {
            Require(cancelled && (!cancelFull || c.FullHashBytesRead > 0), "cancellation must interrupt an active pass");
            return null!;
        }
        Require(result != null && c.FilesDiscovered == expected && !result.HasUncheckedFiles, "file count and completeness");
        return result!;
    }

    private static void Dense(string work, long size)
    {
        // These are fully written regular files: no SetLength, sparse, or virtual metadata.
        string root = Path.Combine(work, "dense-D");
        CheckDenseDataset(root);
        string marker = root + ".generated";
        if (File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing a linked dense dataset marker");
        DatasetGenerator.Generate("D", root, 1, size, copies: 2);
        if (OperatingSystem.IsWindows()) Require(Directory.EnumerateFiles(root).All(p =>
            (File.GetAttributes(p) & (FileAttributes.SparseFile | FileAttributes.Compressed)) == 0), "dense files must be ordinary uncompressed files");
        string cache = Path.Combine(work, "dense-cache.db");
        var full = Measure("dense", "full-exact", root, null, 2, exact: true);
        Require(full.Groups.Single().Verification == VerificationState.ExactMatch &&
            full.Counters.FullHashBytesRead == 2 * size && full.Counters.ExactCompareBytesRead == 2 * size,
            "complete full hash and byte comparison of both dense files");
        ScanCache.Delete(cache);
        Measure("dense", "cancel-full", root, cache, 2, cancelFull: true);
        var resumed = Measure("dense", "after-cancel", root, cache, 2);
        Require(resumed.Groups.Count == 1 && resumed.Counters.FullHashBytesRead == 2 * size, "partial hash must not survive cancellation");
        var warm = Measure("dense", "warm", root, cache, 2);
        Require(warm.Groups.Count == 1 && warm.Counters.ContentBytesRead == 0, "warm dense cache should read no content");
        // Reuse the owned, fully written pair for scenario C without allocating another pair.
        CheckDenseDataset(root);
        string second = Path.Combine(root, "d1.bin");
        using (var writer = File.OpenHandle(second, FileMode.Open, FileAccess.Write))
            RandomAccess.Write(writer, new byte[] { 255, 17, 88, 99 }, size / 2 + 100);
        var changed = Measure("dense", "middle-difference", root, cache, 2);
        Require(changed.Groups.Count == 0 && changed.Counters.FullHashBytesRead == 0 && changed.Counters.QuickHashBytesRead > 0,
            "middle difference must reject cached dense duplicates before full hashing");
        CheckDenseDataset(root);
        foreach (string name in new[] { "d0.bin", "d1.bin" }) File.Delete(Path.Combine(root, name));
        // Nonrecursive removal preserves any unrelated file added after the last check.
        Directory.Delete(root); File.Delete(marker);
    }

    private static void CheckDenseDataset(string root)
    {
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing a linked dense dataset directory");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(path);
            if ((!name.Equals("d0.bin", comparison) && !name.Equals("d1.bin", comparison)) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Refusing an unexpected dense dataset entry: " + path);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void NativeTree(string work, int count)
    {
        string root = Path.Combine(work, "tree"), marker = Path.Combine(work, "tree-count.txt");
        int previous = File.Exists(marker) ? int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture) : 0;
        Require(previous <= count && (previous != 0 || !Directory.Exists(root)), "tree generation must be fresh or extend a completed owned tree");
        var fs = new WindowsFileSystem(); Directory.CreateDirectory(root);
        Require(fs.TryGetVolume(root, out var volume, out _) && fs.TryQueryJournal(volume, out _, out _, out _), "native NTFS journal must be available");
        string FilePath(int index) => Path.Combine(Bucket(index / 10_000), $"file-{index:D8}.bin");
        string Bucket(int bucket) => Path.Combine(root, bucket % 5 == 0 ? "данные" : "data", $"batch-{bucket:D4}");
        var generation = Stopwatch.StartNew(); long generated = previous;
        Parallel.For(previous / 10_000, (count + 9999) / 10_000, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, bucket =>
        {
            Directory.CreateDirectory(Bucket(bucket));
            for (int i = Math.Max(previous, bucket * 10_000); i < Math.Min(count, (bucket + 1) * 10_000); i++)
                DatasetGenerator.CreateSparseFile(FilePath(i), i + 1L);
            long done = Interlocked.Add(ref generated, Math.Min(count, (bucket + 1) * 10_000) - Math.Max(previous, bucket * 10_000));
            if (done % 100_000 == 0) Console.WriteLine($"Native generation: {done}/{count}, {generation.Elapsed.TotalSeconds:F1}s");
        });
        File.WriteAllText(marker, count.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"Native tree generated: {count} actual NTFS sparse files in {generation.Elapsed.TotalSeconds:F1}s");
        string cache = Path.Combine(work, "native-cache.db"); ScanCache.Delete(cache);
        var full = Measure("native", "full-inventory-fill", root, cache, count);
        Unique(full, count);
        Require(full.Counters.FastEnumeratedDirectories > 0, "native bulk enumeration must run");
        var warm = Measure("native", "warm-inventory", root, cache, count); Unique(warm, count);
        Require(warm.Counters.FastEnumeratedDirectories + warm.Counters.FallbackEnumeratedDirectories == 0 &&
            warm.Counters.InventoryDirectoriesReused == full.Counters.DirectoriesScanned, "unchanged real tree must avoid native enumeration");
        string renamed = FilePath(10) + ".renamed";
        File.Move(FilePath(10), renamed); File.Delete(FilePath(0));
        DatasetGenerator.CreateSparseFile(Path.Combine(Bucket(0), "replacement.bin"), count + 1L);
        string pair = Path.Combine(root, "duplicate-pair"); Directory.CreateDirectory(pair);
        foreach (string name in new[] { "a", "b" }) DatasetGenerator.CreateSparseFile(Path.Combine(pair, name), count + 2L);
        var delta = Measure("native", "rename-add-delete", root, cache, count + 2);
        Require(delta.Groups.Count == 1 && delta.Groups[0].UniquePhysicalFileCount == 2 &&
            delta.Counters.FastEnumeratedDirectories + delta.Counters.FallbackEnumeratedDirectories < full.Counters.DirectoriesScanned,
            "changed tree must refresh affected parents and find the new independent pair");
        var fresh = Measure("native", "fresh-oracle", root, null, count + 2);
        Require(fresh.Counters.LogicalBytesDiscovered == delta.Counters.LogicalBytesDiscovered &&
            Signature(fresh) == Signature(delta), "incremental groups and totals must match a fresh full scan");
        Measure("native", "cancel-inventory-replay", root, cache, count + 2, cancel: true);
        var recovered = Measure("native", "after-cancel-warm", root, cache, count + 2);
        Require(Signature(recovered) == Signature(delta) && recovered.Counters.ContentBytesRead == 0 &&
            recovered.Counters.FastEnumeratedDirectories + recovered.Counters.FallbackEnumeratedDirectories == 0,
            "cancellation must preserve the last complete inventory and hashes");
        // Restore the generated baseline so a larger run can extend this owned tree.
        File.Move(renamed, FilePath(10)); File.Delete(Path.Combine(Bucket(0), "replacement.bin"));
        DatasetGenerator.CreateSparseFile(FilePath(0), 1); Directory.Delete(pair, recursive: true);
    }

    private static void Unique(ScanResult r, int count) => Require(r.Counters.UniqueSizeFilesRejected == count &&
        r.Counters.ContentBytesRead == 0 && r.Groups.Count == 0, "all real unique-size files must avoid content I/O");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Workload(string work, int uniqueCount)
    {
        const int sameSize = 20_000, groups = 5_000, aliases = 250;
        string root = Path.Combine(work, "mixed-workload");
        Require(!Directory.Exists(root), "mixed workload generation requires a fresh owned workspace");
        Directory.CreateDirectory(root);
        string deep = root;
        for (int i = 0; i < 5; i++) deep = Path.Combine(deep, "данные-" + new string((char)('a' + i), 45));
        Directory.CreateDirectory(deep);
        Parallel.For(0, (uniqueCount + 999) / 1000, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, bucket =>
        {
            string d = Path.Combine(deep, "batch-" + bucket); Directory.CreateDirectory(d);
            for (int i = bucket * 1000; i < Math.Min(uniqueCount, (bucket + 1) * 1000); i++)
                DatasetGenerator.CreateSparseFile(Path.Combine(d, $"уникальный-{i:D8}.bin"), 1_000_000L + i);
        });
        string rejects = Path.Combine(root, "same-size-rejects"); Directory.CreateDirectory(rejects);
        byte[] bytes = new byte[2048];
        for (int i = 0; i < sameSize; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, i);
            File.WriteAllBytes(Path.Combine(rejects, "file-" + i), bytes);
        }
        string copies = Path.Combine(root, "copies"); Directory.CreateDirectory(copies);
        bytes = new byte[4096];
        string Group(int i) => Path.Combine(copies, "group-" + i);
        for (int i = 0; i < groups; i++)
        {
            string d = Group(i); Directory.CreateDirectory(d);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, i);
            foreach (string name in new[] { "a", "b", "c" }) File.WriteAllBytes(Path.Combine(d, name), bytes);
            if (i % 20 == 0 && !CreateHardLinkW(WindowsFileSystem.ToExtendedPath(Path.Combine(d, "alias")),
                WindowsFileSystem.ToExtendedPath(Path.Combine(d, "a")), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        int files = uniqueCount + sameSize + groups * 3 + aliases;
        string cache = Path.Combine(work, "mixed-cache.db");
        void Membership(ScanResult r, bool changed = false)
        {
            Require(r.Groups.Count == groups && r.Groups.Sum(g => g.UniquePhysicalFileCount) == groups * 3 - (changed ? 1 : 0) &&
                r.Groups.SelectMany(g => g.Files).Sum(f => f.HardLinkAliasCount) == aliases,
                "mixed workload physical groups or alias accounting");
        }
        var full = Measure("workload", "full-inventory-fill", root, cache, files); Membership(full);
        Require(full.Counters.UniqueSizeFilesRejected == uniqueCount, "mixed unique-size membership");
        var warm = Measure("workload", "warm-inventory", root, cache, files); Membership(warm);
        Require(warm.Counters.ContentBytesRead == 0 && warm.Counters.FastEnumeratedDirectories + warm.Counters.FallbackEnumeratedDirectories == 0,
            "mixed warm inventory must avoid content reads and enumeration");
        File.Move(Path.Combine(Group(1), "a"), Path.Combine(Group(1), "renamed"));
        File.Delete(Path.Combine(Group(1), "c")); DatasetGenerator.CreateSparseFile(Path.Combine(root, "new-unique"), 5_000_000);
        var delta = Measure("workload", "rename-add-delete", root, cache, files); Membership(delta, true);
        var fresh = Measure("workload", "fresh-oracle", root, null, files); Membership(fresh, true);
        Require(Signature(delta) == Signature(fresh) && delta.Counters.LogicalBytesDiscovered == fresh.Counters.LogicalBytesDiscovered,
            "mixed incremental membership differs from fresh scan");
        Measure("workload", "cancel-inventory-replay", root, cache, files, cancel: true);
        var recovered = Measure("workload", "after-cancel-warm", root, cache, files); Membership(recovered, true);
        Require(Signature(recovered) == Signature(delta) && recovered.Counters.ContentBytesRead == 0,
            "mixed cache was damaged by cancellation");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
    private static string Signature(ScanResult r) => string.Join("|", r.Groups.Select(g =>
        g.LogicalSize + ":" + g.Hash + ":" + string.Join(",", g.Files.Select(f => f.Path).Order(StringComparer.Ordinal))).Order(StringComparer.Ordinal));
    private static void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
}
