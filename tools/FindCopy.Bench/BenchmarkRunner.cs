using FindCopy.Core;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FindCopy.Bench;

public static class BenchmarkRunner
{
    private const string Help = """
        FindCopy.Bench <work-dir> [options]
          --scenario A|B|C|D|E|F|G|H|all
            A unique sizes; B differences at start; C differences in middle; D large duplicates
            E HDD, F SATA SSD, G NVMe, H network: require --path and run a settings sweep
          --acceptance             A: 1 million files; B: 100,000; C: 1 GiB; D: 10 GiB
          --scale N                multiply generated workloads (default 1)
          --count N                explicit file count for A/B
          --copies N               independent C/D files (default 4)
          --size-mib N             64-bit C/D file size
          --sparse                 sparse C/D data: logical streaming test, not disk throughput
          --metadata-files N       virtual unique-size metadata RAM test; no native I/O
          --generate-only          prepare data without scanning
          --threshold KiB          small-file threshold (default 1024)
          --readers N              fixed full-read concurrency; disables autotuning
          --buffer KiB             streaming buffer (default 1024)
          --sample KiB             quick sample (default 64)
          --sweep                  readers 1/2/4 × thresholds 256/1024/4096 × buffers 1024/4096
          --enum auto|win32         native Windows enumeration backend
          --ads                    compare named streams
          --exact                  stream exact comparisons too
          --cache                  cold fingerprint fill and warm fingerprint run
          --path <folder>          existing dataset; physical E-H drives supplied by the operator
          --os-cache-state <label>  observed cache condition (default unspecified; never auto-evicted)
          --label <text>           hardware/dataset description supplied by the operator
          <work-dir> --extended-acceptance dense [size-MiB]   full hash/exact/cancel/cache on fully written files
          <work-dir> --extended-acceptance native [count]     actual NTFS files, inventory/deltas/cancellation
        Results append to <work-dir>/bench-results-v3.csv. Reported bytes are logical content I/O.
        """;

    public static int Execute(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?") { Console.WriteLine(Help); return 0; }
        try { return Execute(Options.Parse(args)); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { Console.Error.WriteLine("Invalid arguments: " + ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine("Benchmark failed: " + ex.Message); return 1; }
    }

    private static int Execute(Options o)
    {
        Directory.CreateDirectory(o.Work);
        var targets = new List<(string Scenario, string Directory, string Kind)>();
        if (o.Path != null) targets.Add((o.Scenario == "ALL" ? "PATH" : o.Scenario, o.Path, "existing"));
        else if (o.MetadataCount > 0) targets.Add(("A", System.IO.Path.Combine(o.Work, "metadata-fixture"), "virtual_metadata"));
        else foreach (string scenario in o.Scenario == "ALL" ? new[] { "A", "B", "C", "D" } : new[] { o.Scenario })
        {
            int count = o.Count;
            long size = o.SizeMiB * 1048576;
            if (o.Acceptance)
            {
                if (count == 0 && scenario is "A" or "B") count = checked((scenario == "A" ? 1_000_000 : 100_000) * o.Scale);
                if (size == 0 && scenario is "C" or "D") size = checked((scenario == "C" ? 1L : 10L) * 1073741824 * o.Scale);
            }
            string dir = System.IO.Path.Combine(o.Work, $"scenario_{scenario}_v3_x{o.Scale}_bytes{size}_n{count}_copies{o.Copies}_sparse{o.Sparse}");
            DatasetGenerator.Generate(scenario, dir, o.Scale, size, count, o.Copies, o.Sparse);
            targets.Add((scenario, dir, scenario == "A" || scenario == "B" || o.Sparse ? "generated_sparse" : "generated_dense"));
        }
        if (o.GenerateOnly) return 0;
        string csv = System.IO.Path.Combine(o.Work, "bench-results-v3.csv");
        if (!File.Exists(csv)) File.WriteAllText(csv, "utc;scenario;run;dataset_kind;label;os_cache_state;threshold_kib;reader_limit;buffer_kib;sample_kib;cpu_budget;files;logical_bytes;elapsed_s;files_per_s;native_directories;inventory_directories;enum_files_per_s;content_bytes;mb_per_s;cpu_s;cpu_percent;peak_scan_ram_mib;cache_hit_percent;read_amplification;groups;skipped;errors;changed;complete;storage;os;architecture;cpu_count;available_memory_mib\n");
        bool failed = false;
        foreach (var target in targets)
        foreach (int readers in o.Sweep ? new[] { 1, 2, 4 } : new[] { o.Readers })
        foreach (int threshold in o.Sweep ? new[] { 256, 1024, 4096 } : new[] { o.Threshold })
        foreach (int buffer in o.Sweep ? new[] { 1024, 4096 } : new[] { o.Buffer })
        {
            failed |= !Run(o, target, csv, "nocache", null, readers, threshold, buffer);
            if (o.Cache)
            {
                string cache = System.IO.Path.Combine(o.Work, "bench-cache.db"); ScanCache.Delete(cache);
                failed |= !Run(o, target, csv, "cache-fill", cache, readers, threshold, buffer);
                failed |= !Run(o, target, csv, "cache-warm", cache, readers, threshold, buffer);
            }
        }
        Console.WriteLine("Results: " + csv);
        Console.WriteLine("No OS caches were evicted. Virtual/sparse measurements do not establish physical device throughput.");
        return failed ? 1 : 0;
    }

    private static bool Run(Options o, (string Scenario, string Directory, string Kind) target, string csv, string run,
        string? cache, int readers, int threshold, int buffer)
    {
        GC.Collect();
        using var process = Process.GetCurrentProcess(); var cpuStart = process.TotalProcessorTime;
        var tuning = new Tuning { SmallFileThreshold = threshold * 1024L, SampleSize = o.Sample * 1024, StreamBufferSize = buffer * 1024,
            FullReadersHdd = readers == 0 ? 1 : readers, FullReadersSsd = readers == 0 ? 2 : readers,
            FullReadersNvme = readers == 0 ? 4 : readers, FullReadersNvmeStart = readers == 0 ? 2 : readers,
            FullReadersNetwork = readers == 0 ? 1 : readers, FullReadersUnknown = readers == 0 ? 1 : readers,
            AutotuneNvme = readers == 0 };
        MetadataFileSystem? fixture = o.MetadataCount > 0 ? new MetadataFileSystem(target.Directory, o.MetadataCount) : null;
        long peak = process.WorkingSet64;
        using var sampler = new Timer(_ =>
        {
            using var p = Process.GetCurrentProcess(); long current = p.WorkingSet64, old;
            do { old = Interlocked.Read(ref peak); if (current <= old) break; }
            while (Interlocked.CompareExchange(ref peak, current, old) != old);
        }, null, 0, 10);
        var watch = Stopwatch.StartNew();
        var r = new ScanController(fixture).RunAsync(new ScanOptions { Roots = new[] { target.Directory }, CachePath = cache,
            Tuning = tuning, EnumerationBackend = o.Backend, CompareAlternateStreams = o.Ads, ExactVerification = o.Exact }).GetAwaiter().GetResult();
        watch.Stop(); sampler.DisposeAsync().AsTask().GetAwaiter().GetResult(); process.Refresh();
        var c = r.Counters; double elapsed = watch.Elapsed.TotalSeconds, cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        double enumTime = r.PhaseTimes.FirstOrDefault(p => p.Phase.StartsWith("Обход")).Duration.TotalSeconds;
        long lookups = c.CacheHits + c.CacheMisses;
        string storage = string.Join(" | ", r.Storage.Select(s => $"{s.Kind}:{s.DomainKey}:{s.Description}"));
        double ram = Math.Max(Interlocked.Read(ref peak), process.WorkingSet64) / 1048576.0;
        object[] row = { DateTime.UtcNow.ToString("O"), target.Scenario, run, target.Kind, o.Label, o.CacheState, threshold, readers,
            buffer, o.Sample, tuning.CpuBudget, c.FilesDiscovered, c.LogicalBytesDiscovered, elapsed, c.FilesDiscovered / Math.Max(elapsed, 1e-6),
            c.FastEnumeratedDirectories + c.FallbackEnumeratedDirectories, c.InventoryDirectoriesReused,
            enumTime > 0 ? c.FilesDiscovered / enumTime : 0, c.ContentBytesRead, c.ContentBytesRead / 1048576.0 / Math.Max(elapsed, 1e-6),
            cpu, 100 * cpu / Math.Max(elapsed * Environment.ProcessorCount, 1e-6), ram,
            lookups == 0 ? 0 : 100.0 * c.CacheHits / lookups, c.ReadAmplification, r.Groups.Count,
            c.SkippedFiles + c.SkippedDirectories, c.ErrorFiles, c.ChangedFiles, !r.HasUncheckedFiles, storage,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576 };
        File.AppendAllText(csv, string.Join(";", row.Select(Cell)) + "\n");
        Console.WriteLine($"{target.Scenario} {run}: {c.FilesDiscovered:N0} files, {elapsed:F3}s, {c.ContentBytesRead:N0} content bytes, {ram:F0} MiB, {r.Groups.Count} groups, readers={readers}, threshold={threshold}, buffer={buffer}");
        bool correct = c.ErrorFiles == 0 && c.ChangedFiles == 0;
        if (fixture != null) correct &= c.FilesDiscovered == o.MetadataCount && c.UniqueSizeFilesRejected == o.MetadataCount && c.ContentBytesRead == 0 && fixture.ContentOpens == 0;
        if (target.Scenario == "A" && target.Kind != "existing") correct &= c.ContentBytesRead == 0 && r.Groups.Count == 0;
        if (target.Scenario is "B" or "C" && target.Kind != "existing") correct &= r.Groups.Count == 0;
        if (target.Scenario == "D" && target.Kind != "existing") correct &= r.Groups.Count == 1 && r.Groups[0].UniquePhysicalFileCount == o.Copies;
        if (!correct) Console.Error.WriteLine("Run failed a completeness or generated-dataset acceptance check.");
        return correct;
    }

    private static string Cell(object value)
    {
        string s = value is double d ? d.ToString("0.######", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private sealed class Options
    {
        public string Work = "", Scenario = "ALL", Label = "", CacheState = "unspecified";
        public string? Path;
        public int Scale = 1, Count, Copies = 4, MetadataCount, Threshold = 1024, Readers, Buffer = 1024, Sample = 64;
        public long SizeMiB;
        public bool Cache, Ads, Exact, GenerateOnly, Sparse, Sweep, Acceptance;
        public EnumerationBackend Backend;
        public static Options Parse(string[] args)
        {
            var o = new Options { Work = System.IO.Path.GetFullPath(args[0]) };
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value");
                int Number() => int.Parse(Next(), CultureInfo.InvariantCulture);
                switch (args[i])
                {
                    case "--scenario": o.Scenario = Next().ToUpperInvariant(); break;
                    case "--scale": o.Scale = Number(); break;
                    case "--count": o.Count = Number(); break;
                    case "--copies": o.Copies = Number(); break;
                    case "--size-mib": o.SizeMiB = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--metadata-files": o.MetadataCount = Number(); break;
                    case "--threshold": o.Threshold = Number(); break;
                    case "--readers": o.Readers = Number(); break;
                    case "--buffer": o.Buffer = Number(); break;
                    case "--sample": o.Sample = Number(); break;
                    case "--path": o.Path = System.IO.Path.GetFullPath(Next()); break;
                    case "--label": o.Label = Next(); break;
                    case "--os-cache-state": o.CacheState = Next(); break;
                    case "--cache": o.Cache = true; break;
                    case "--ads": o.Ads = true; break;
                    case "--exact": o.Exact = true; break;
                    case "--generate-only": o.GenerateOnly = true; break;
                    case "--sparse": o.Sparse = true; break;
                    case "--sweep": o.Sweep = true; break;
                    case "--acceptance": o.Acceptance = true; break;
                    case "--enum": o.Backend = Next().ToLowerInvariant() switch { "auto" => EnumerationBackend.Auto, "win32" => EnumerationBackend.Win32Only, _ => throw new ArgumentException("Unknown enumeration backend") }; break;
                    default: throw new ArgumentException("Unknown option: " + args[i]);
                }
            }
            if (o.Scenario is not ("A" or "B" or "C" or "D" or "E" or "F" or "G" or "H" or "ALL")) throw new ArgumentException("Unknown scenario");
            if (o.Scale <= 0 || o.Count < 0 || o.Count > 50_000_000 || o.Copies < 2 || o.Copies > 1_000_000 ||
                o.SizeMiB < 0 || o.SizeMiB > long.MaxValue / 1048576 || o.MetadataCount < 0 || o.MetadataCount > 50_000_000 ||
                o.Threshold < 0 || o.Buffer <= 0 || o.Buffer > 16384 || o.Sample <= 0 || o.Sample > 1024 || o.Readers < 0 || o.Readers > 64)
                throw new ArgumentException("Sizes, counts, and worker budgets are out of range");
            if (o.Scenario is "E" or "F" or "G" or "H")
            { if (o.Path == null) throw new ArgumentException("E-H require an existing device dataset through --path"); o.Sweep = true; }
            if (o.MetadataCount > 0 && (o.Scenario != "A" || o.Path != null || o.GenerateOnly || o.Cache))
                throw new ArgumentException("--metadata-files requires scenario A without --path, --cache, or --generate-only");
            if (o.Path != null && !Directory.Exists(o.Path)) throw new ArgumentException("Dataset path does not exist");
            return o;
        }
    }
}
