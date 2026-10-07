using System.Diagnostics;
using System.Globalization;
using FindCopy.Core;

// Benchmark harness (ТЗ §28). Generates synthetic datasets and reports, per run:
// elapsed, files/s, enumeration rate, bytes read, MB/s, CPU time, peak RAM, cache hit rate, read amplification.
//
//   FindCopy.Bench <work-dir> [--scenario A|B|C|D|all] [--scale N] [--threshold KiB]
//                  [--readers N] [--buffer KiB] [--cache] [--path <existing folder>]
//
// --path benchmarks a real folder instead of generated data (e.g. on HDD / SSD / NVMe / network share).

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("""
        FindCopy.Bench <work-dir> [options]
          --scenario A|B|C|D|all  A: many files with unique sizes (expect ~0 content I/O)
                                  B: same size, differ at the start (expect ~64 KiB per file)
                                  C: large files, same header/footer, differ in the middle (Q3)
                                  D: real large duplicates (sequential BLAKE3 throughput)
          --scale N               multiplier for dataset size (default 1)
          --threshold KiB         SMALL_FILE_THRESHOLD (default 1024)
          --readers N             fixed full-read concurrency for every storage kind (disables autotune)
          --buffer KiB            streaming buffer (default 1024)
          --sample KiB            quick sample size (default 64)
          --enum auto|win32       directory enumeration backend (default auto)
          --ads                   also compare NTFS alternate streams
          --cache                 also run twice with the persistent cache (cold fill + warm)
          --path <folder>         benchmark an existing folder instead of generated data
        Results are appended to <work-dir>/bench-results.csv
        """);
    return 0;
}

string work = Path.GetFullPath(args[0]);
string scenario = "ALL"; int scale = 1; long thresholdKiB = 1024; int readers = 0; int bufferKiB = 1024; bool useCache = false; string? realPath = null;
int sampleKiB = 64; var backend = EnumerationBackend.Auto; bool ads = false;
for (int i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
    switch (args[i])
    {
        case "--scenario": scenario = Next().ToUpperInvariant(); break;
        case "--scale": scale = int.Parse(Next(), inv); break;
        case "--threshold": thresholdKiB = long.Parse(Next(), inv); break;
        case "--readers": readers = int.Parse(Next(), inv); break;
        case "--buffer": bufferKiB = int.Parse(Next(), inv); break;
        case "--cache": useCache = true; break;
        case "--sample": sampleKiB = int.Parse(Next(), inv); break;
        case "--enum": backend = Next().ToLowerInvariant() == "win32" ? EnumerationBackend.Win32Only : EnumerationBackend.Auto; break;
        case "--ads": ads = true; break;
        case "--path": realPath = Path.GetFullPath(Next()); break;
        default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
    }
}
Directory.CreateDirectory(work);

var tuning = readers > 0
    ? new Tuning
    {
        SmallFileThreshold = thresholdKiB * 1024, StreamBufferSize = bufferKiB * 1024, SampleSize = sampleKiB * 1024,
        FullReadersHdd = readers, FullReadersSsd = readers, FullReadersNvme = readers, FullReadersNetwork = readers,
        FullReadersUnknown = readers, AutotuneNvme = false,
    }
    : new Tuning { SmallFileThreshold = thresholdKiB * 1024, StreamBufferSize = bufferKiB * 1024, SampleSize = sampleKiB * 1024 };

var targets = new List<(string Name, string Dir)>();
if (realPath != null) targets.Add(("PATH", realPath));
else
{
    foreach (var s in scenario == "ALL" ? new[] { "A", "B", "C", "D" } : new[] { scenario })
        targets.Add((s, Generate(s, Path.Combine(work, "scenario_" + s + "_x" + scale), scale)));
}

string csv = Path.Combine(work, "bench-results.csv");
if (!File.Exists(csv))
    File.WriteAllText(csv, "time;scenario;run;threshold_kib;readers;buffer_kib;files;elapsed_s;files_per_s;enum_files_per_s;bytes_read;mb_per_s;cpu_s;peak_ram_mb;cache_hit_rate;read_amplification;groups;storage\n");

Console.WriteLine($"{"scenario",-9}{"run",-8}{"files",10}{"elapsed",10}{"files/s",11}{"enum/s",11}{"read MB",10}{"MB/s",9}{"cpu s",8}{"RAM MB",8}{"hit%",7}{"ampl",9}{"groups",8}");
foreach (var (name, dir) in targets)
{
    Run(name, dir, "nocache", null);
    if (useCache)
    {
        string cachePath = Path.Combine(work, "bench-cache.db");
        ScanCache.Delete(cachePath);
        Run(name, dir, "cache-1", cachePath);
        Run(name, dir, "cache-2", cachePath);
    }
}
Console.WriteLine("\nNote: the OS file cache keeps recently written/read data in RAM; for cold-disk numbers reboot or use a dataset larger than RAM.");
Console.WriteLine("Results appended to " + csv);
return 0;

void Run(string name, string dir, string run, string? cache)
{
    GC.Collect();
    var proc = Process.GetCurrentProcess();
    var cpu0 = proc.TotalProcessorTime;
    var options = new ScanOptions
    {
        Roots = new[] { dir }, Recursive = true, Tuning = tuning, CachePath = cache,
        EnumerationBackend = backend, CompareAlternateStreams = ads,
    };
    var sw = Stopwatch.StartNew();
    var r = new ScanController().RunAsync(options).GetAwaiter().GetResult();
    sw.Stop();
    proc.Refresh();
    double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds;
    double secs = sw.Elapsed.TotalSeconds;
    var c = r.Counters;
    var enumTime = r.PhaseTimes.FirstOrDefault(p => p.Phase.StartsWith("Обход")).Duration.TotalSeconds;
    double filesPerS = c.FilesDiscovered / Math.Max(secs, 1e-6);
    double enumRate = enumTime > 0 ? c.FilesDiscovered / enumTime : 0;
    long read = c.ContentBytesRead;
    double mbps = read / 1048576.0 / Math.Max(secs, 1e-6);
    long lookups = c.CacheHits + c.CacheMisses;
    double hit = lookups == 0 ? 0 : 100.0 * c.CacheHits / lookups;
    double ram = proc.PeakWorkingSet64 / 1048576.0;
    string storage = string.Join(",", r.Storage.Select(s => s.Description));
    Console.WriteLine($"{name,-9}{run,-8}{c.FilesDiscovered,10}{secs,10:0.00}{filesPerS,11:0}{enumRate,11:0}{read / 1048576.0,10:0.0}{mbps,9:0}{cpu,8:0.0}{ram,8:0}{hit,7:0}{c.ReadAmplification,9:0.0000}{r.Groups.Count,8}");
    File.AppendAllText(csv, string.Join(";", DateTime.Now.ToString("s"), name, run, thresholdKiB, readers, bufferKiB, c.FilesDiscovered,
        secs.ToString("0.000", inv), filesPerS.ToString("0", inv), enumRate.ToString("0", inv), read, mbps.ToString("0.0", inv), cpu.ToString("0.00", inv),
        ram.ToString("0", inv), hit.ToString("0.0", inv), c.ReadAmplification.ToString("0.000000", inv), r.Groups.Count, storage) + "\n");
}

static string Generate(string s, string dir, int scale)
{
    string marker = dir + ".generated";   // outside the scanned folder
    if (File.Exists(marker)) return dir;
    Directory.CreateDirectory(dir);
    Console.WriteLine($"Generating scenario {s} in {dir} ...");
    var rnd = new Random(12345);
    const int MiB = 1 << 20;
    switch (s)
    {
        case "A": // many files, almost all unique sizes
        {
            int n = 20000 * scale;
            var buf = new byte[n + 1];
            for (int i = 1; i <= n; i++)
            {
                string sub = Path.Combine(dir, (i % 100).ToString("00"));
                if (i <= 100) Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(sub, $"f{i}.bin"), buf.AsSpan(0, i).ToArray());
            }
            break;
        }
        case "B": // same size, differ at the start
        {
            int n = 200 * scale;
            var data = new byte[3 * MiB / 2];
            for (int i = 0; i < n; i++)
            {
                rnd.NextBytes(data.AsSpan(0, 4096));
                File.WriteAllBytes(Path.Combine(dir, $"b{i}.bin"), data);
            }
            break;
        }
        case "C": // large files, same header/footer, different middle
        {
            int n = 4, size = 64 * MiB * scale;
            var data = new byte[size];
            rnd.NextBytes(data);
            for (int i = 0; i < n; i++)
            {
                data[size / 2 + 100] = (byte)i;
                File.WriteAllBytes(Path.Combine(dir, $"c{i}.bin"), data);
            }
            break;
        }
        case "D": // real large duplicates
        {
            int size = 256 * MiB * scale;
            var data = new byte[size];
            rnd.NextBytes(data);
            for (int i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(dir, $"d{i}.bin"), data);
            break;
        }
        default: throw new ArgumentException("unknown scenario " + s);
    }
    File.WriteAllText(marker, "ok");
    return dir;
}
