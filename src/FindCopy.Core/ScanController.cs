using System.Diagnostics;

namespace FindCopy.Core;

/// <summary>
/// Orchestrates the pipeline (ТЗ §3, §31):
/// Enumerate → size groups → hard-link identity → Q1 → Q2 → Q3 → Q4 → full BLAKE3 →
/// HASH_MATCH groups → optional byte-for-byte ExactVerifier.
/// Every stage only touches groups that still contain at least two physical files.
/// </summary>
public sealed class ScanController
{
    private readonly IFileSystem? _fs;
    private readonly IQuickHasher _quick;
    private readonly IFullHasher _full;

    public ScanCounters Counters { get; private set; } = new();

    public ScanController(IFileSystem? fs = null, IQuickHasher? quick = null, IFullHasher? full = null)
    {
        _fs = fs;
        _quick = quick ?? Xxh3QuickHasher.Instance;
        _full = full ?? Blake3FullHasher.Instance;
    }

    public Task<ScanResult> RunAsync(ScanOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Tuning.Validate();
        Counters = new ScanCounters();
        var counters = Counters;
        // A fresh platform layer per run, so backend choice and per-run statistics do not leak between scans.
        var fs = _fs ?? (OperatingSystem.IsWindows()
            ? new WindowsFileSystem { Backend = options.EnumerationBackend }
            : new PortableFileSystem());
        return Task.Factory.StartNew(() => new ScanRun(fs, _quick, _full, options, counters, ct).Execute(),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}

internal sealed class ScanRun
{
    private readonly IFileSystem _fs;
    private readonly IQuickHasher _quick;
    private readonly IFullHasher _full;
    private readonly ScanOptions _opt;
    private readonly Tuning _t;
    private readonly ScanCounters _c;
    private readonly CancellationToken _ct;

    private readonly RecordList _records = new();
    private readonly PathStore _paths = new();
    private readonly ErrorCollector _errors = new();
    private readonly StorageDomains _domains = new();
    private readonly IoScheduler _io;
    private readonly Dictionary<int, List<int>> _aliases = new();
    private byte[] _fullHashes = Array.Empty<byte>();
    private List<int[]> _zeroGroups = new();
    private ScanCache? _cache;
    private DirectoryInventory? _inventory;
    private readonly Dictionary<int, byte[]> _cachedFull = new();
    private readonly Dictionary<int, CacheEntry> _cachedQuick = new();
    private readonly List<string> _volumeProbePaths = new();
    private readonly List<(ulong Vol, ulong JournalId, long NextUsn)> _usnToSave = new();

    public ScanRun(IFileSystem fs, IQuickHasher quick, IFullHasher full, ScanOptions opt, ScanCounters c, CancellationToken ct)
    {
        _fs = fs; _quick = quick; _full = full; _opt = opt; _t = opt.Tuning; _c = c; _ct = ct;
        _io = new IoScheduler(_domains, _t.CpuBudget)
        {
            AutotuneSettled = (domain, workers) =>
            {
                var p = _domains[domain];
                c.AutotuneNote = $"{p.Description} ({p.DomainKey}): {workers} {(workers == 1 ? "поток" : "потока")} полного чтения";
            },
        };
    }

    public ScanResult Execute()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (_opt.CachePath != null)
            {
                OpenCache();
                if (_cache != null && _opt.UseUsnJournal && !_opt.FollowDirectoryReparsePoints && _fs is IUsnInventorySource journal)
                {
                    try { _inventory = new DirectoryInventory(_fs, journal, _cache, _c); }
                    catch (Exception ex) { _c.CacheNote = "Снимок каталогов недоступен: " + ex.Message; }
                }
            }
            Phase("Обход папок");
            Enumerate();
            CopyEnumerationStats();
            Phase("Группировка по размеру");
            var (groups, zero) = GroupBySize();
            Phase("Проверка жёстких ссылок");
            groups = ResolvePhysicalIdentity(groups);
            if (zero.Count >= 2) _zeroGroups = ResolvePhysicalIdentity(new List<int[]> { zero.ToArray() });
            if (_cache != null)
            {
                Phase("Чтение кеша");
                if (_opt.UseUsnJournal) ApplyUsnJournal();
                LoadFromCache(groups);
            }
            var result = RunContentStages(groups, zero, sw);
            SaveUsnState();
            if (_cache != null) _inventory?.Commit(_ct);
            result.Counters.CacheNote = _c.CacheNote;
            return result;
        }
        finally
        {
            _cache?.Dispose();
        }
    }

    private ScanResult RunContentStages(List<int[]> groups, List<int> zero, Stopwatch sw)
    {
        var small = groups.Where(g => _records[g[0]].Size <= _t.SmallFileThreshold).ToList();
        var large = groups.Where(g => _records[g[0]].Size > _t.SmallFileThreshold).ToList();

        long S = _t.SampleSize;
        static long Align(long v) => v & ~4095L;
        large = QuickStage(large, 1, "Быстрая проверка: начало файлов", sz => 0);
        large = QuickStage(large, 2, "Быстрая проверка: конец файлов", sz => Math.Max(0, sz - S));
        large = QuickStage(large, 3, "Быстрая проверка: середина файлов", sz => sz >= _t.MiddleSampleMinSize ? Align((sz - S) / 2) : -1);
        large = QuickStage(large, 4, "Быстрая проверка: 25%", sz => sz >= _t.QuarterSamplesMinSize ? Align((sz - S) / 4) : -1);
        large = QuickStage(large, 5, "Быстрая проверка: 75%", sz => sz >= _t.QuarterSamplesMinSize ? Align((sz - S) / 4 * 3) : -1);

        Phase("Полный хеш BLAKE3");
        var candidates = small.Concat(large).ToList();
        var hashGroups = FullHashStage(candidates);
        if (_opt.CompareAlternateStreams && hashGroups.Count > 0)
        {
            Phase("Сравнение альтернативных потоков NTFS");
            hashGroups = AlternateStreamStage(hashGroups);
        }
        Interlocked.Exchange(ref _c.HashMatchGroups, hashGroups.Count);

        var verified = new List<(int[] Members, VerificationState State)>();
        if (_opt.ExactVerification)
        {
            Phase("Побайтовая проверка");
            foreach (var g in ExactVerify(hashGroups))
                verified.Add((g, VerificationState.ExactMatch));
            Interlocked.Exchange(ref _c.ExactMatchGroups, verified.Count);
        }
        else
        {
            verified.AddRange(hashGroups.Select(g => (g, VerificationState.HashMatch)));
        }

        // Validate live metadata even for cached hashes before publishing a result.
        foreach (int rec in verified.SelectMany(g => g.Members).Distinct()) ValidateVersion(rec);
        verified = verified.Select(g => (Members: g.Members.Where(i => _records[i].Status == FileStatus.Ok).ToArray(), g.State))
            .Where(g => g.Members.Length >= 2).ToList();
        Interlocked.Exchange(ref _c.HashMatchGroups, verified.Count);
        Interlocked.Exchange(ref _c.ExactMatchGroups, verified.Count(g => g.State == VerificationState.ExactMatch));
        foreach (int rec in _zeroGroups.SelectMany(g => g)) ValidateVersion(rec);
        _zeroGroups = _zeroGroups.Select(g => g.Where(i => _records[i].Status == FileStatus.Ok).ToArray()).Where(g => g.Length >= 2).ToList();
        Phase("Готово");
        var result = BuildResult(verified, zero, sw.Elapsed);
        return result;
    }

    private readonly List<(string Phase, TimeSpan Duration)> _phaseTimes = new();
    private readonly Stopwatch _phaseClock = Stopwatch.StartNew();
    private string? _currentPhase;

    private void Phase(string name)
    {
        _ct.ThrowIfCancellationRequested();
        if (_currentPhase != null) _phaseTimes.Add((_currentPhase, _phaseClock.Elapsed));
        _currentPhase = name;
        _phaseClock.Restart();
        _c.Phase = name;
        Interlocked.Exchange(ref _c.StageDone, 0);
        Interlocked.Exchange(ref _c.StageTotal, 0);
    }

    private string PathOf(int rec) => _paths.GetFullPath(_records[rec]);

    private void Fail(int rec, FileStatus status, string? message = null)
    {
        ref var r = ref _records[rec];
        r.Status = status;
        if (status == FileStatus.Cancelled) return;
        _errors.Add(PathOf(rec), status, message);
        if (status == FileStatus.ChangedDuringScan) Interlocked.Increment(ref _c.ChangedFiles);
        else if (status is FileStatus.CloudContentNotLocal or FileStatus.ReparseSkipped) Interlocked.Increment(ref _c.SkippedFiles);
        else Interlocked.Increment(ref _c.ErrorFiles);
    }

    // ------------------------------------------------------------------ Enumerate (ТЗ §4, §13, §14)

    private List<string> NormalizeRoots()
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = _opt.Roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(_fs.NormalizeRoot).Distinct(cmp).ToList();
        if (!_opt.Recursive) return roots;
        // Drop roots nested inside another root so nothing is scanned twice.
        return roots.Where(r => !roots.Any(o => !cmp.Equals(o, r) &&
            r.StartsWith(o.EndsWith(Path.DirectorySeparatorChar) ? o : o + Path.DirectorySeparatorChar, comparison))).ToList();
    }

    private HashSet<string> BuildExclusions()
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var list = _opt.ExcludedDirectories;
        if (list == null && _opt.SkipSystem && OperatingSystem.IsWindows())
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            list = string.IsNullOrEmpty(win) ? Array.Empty<string>() : new[] { win };
        }
        var set = new HashSet<string>(cmp);
        foreach (var d in list ?? Array.Empty<string>())
        {
            try { set.Add(_fs.NormalizeRoot(d)); } catch { }
        }
        return set;
    }

    private void Enumerate()
    {
        var excluded = BuildExclusions();
        var visited = new HashSet<(ulong, ulong, ulong)>();
        var stack = new Stack<int>();
        int curDir = 0, curDomain = 0;
        string curPath = "";

        DirEntryHandler handler = (ReadOnlySpan<char> name, in EntryInfo e) =>
        {
            uint a = e.Attributes;
            if (e.IsDirectory)
            {
                if (!_opt.Recursive) return;
                string child = PathStore.CombinePath(curPath, name);
                if ((_opt.SkipSystem && (a & FileAttr.System) != 0) || (_opt.SkipHidden && (a & FileAttr.Hidden) != 0) || excluded.Contains(child))
                {
                    _errors.Add(child, FileStatus.PolicySkipped, "Папка исключена настройками поиска");
                    Interlocked.Increment(ref _c.SkippedDirectories);
                    return;
                }
                if ((a & FileAttr.NotLocalMask) != 0 && !_opt.IncludeOnlineOnlyFiles)
                {
                    _errors.Add(child, FileStatus.CloudContentNotLocal, "Облачная папка не обходится без разрешения");
                    Interlocked.Increment(ref _c.SkippedDirectories);
                    return;
                }
                int domain = curDomain;
                bool isLink = (a & FileAttr.ReparsePoint) != 0 && !ReparseTags.IsCloud(e.ReparseTag);
                if (isLink)
                {
                    if (!_opt.FollowDirectoryReparsePoints)
                    {
                        _errors.Add(child, FileStatus.ReparseSkipped, "Ссылка на папку (junction / symlink / точка монтирования) не обходится");
                        return;
                    }
                    domain = _domains.GetOrAdd(_fs.GetStorageProfile(child));
                    _volumeProbePaths.Add(child);
                }
                if (_opt.FollowDirectoryReparsePoints)
                {
                    // VisitedDirectoryIdentitySet: protects against loops and re-scanning (ТЗ §13).
                    if (!_fs.TryGetDirectoryIdentity(child, out var id))
                    {
                        _errors.Add(child, FileStatus.Unsupported, "Без identity папки безопасный обход ссылок невозможен");
                        Interlocked.Increment(ref _c.SkippedDirectories);
                        return;
                    }
                    if (!visited.Add(id))
                    {
                        _errors.Add(child, FileStatus.ReparseSkipped, "Папка уже просканирована (цикл ссылок)");
                        return;
                    }
                }
                stack.Push(_paths.AddDirectory(child, domain));
                return;
            }

            if (_opt.SkipSystem && (a & FileAttr.System) != 0)
            {
                _errors.Add(PathStore.CombinePath(curPath, name), FileStatus.PolicySkipped, "Системный файл исключён настройками поиска");
                Interlocked.Increment(ref _c.SystemSkipped);
                Interlocked.Increment(ref _c.SkippedFiles);
                return;
            }
            if (_opt.SkipHidden && (a & FileAttr.Hidden) != 0)
            {
                _errors.Add(PathStore.CombinePath(curPath, name), FileStatus.PolicySkipped, "Скрытый файл исключён настройками поиска");
                Interlocked.Increment(ref _c.SkippedFiles);
                return;
            }
            if ((a & FileAttr.Device) != 0) return;
            if ((a & FileAttr.ReparsePoint) != 0 && ReparseTags.IsNameSurrogateLink(e.ReparseTag))
            {
                // A file symlink is not a separate physical copy of its target (ТЗ §13).
                _errors.Add(PathStore.CombinePath(curPath, name), FileStatus.ReparseSkipped, "Символическая ссылка на файл");
                Interlocked.Increment(ref _c.SkippedFiles);
                return;
            }
            if ((a & FileAttr.NotLocalMask) != 0 && !_opt.IncludeOnlineOnlyFiles)
            {
                // Never trigger cloud hydration without consent (ТЗ §14).
                _errors.Add(PathStore.CombinePath(curPath, name), FileStatus.CloudContentNotLocal, "Файл хранится только в облаке");
                Interlocked.Increment(ref _c.SkippedFiles);
                return;
            }

            var rec = new FileRecord
            {
                NameHandle = _paths.AddName(name),
                NameLength = (ushort)name.Length,
                DirIndex = curDir,
                Size = e.Size,
                AllocatedSize = -1,
                LastWriteTicks = e.LastWriteTicks,
                Attributes = a,
                ReparseTag = e.ReparseTag,
                PhysicalRep = -1,
                FullHashSlot = -1,
            };
            if (e.HasIdentity)
            {
                // Bulk identity from the directory listing: no need to open the file later.
                rec.Flags = FileRecord.FlagHasIdentity | FileRecord.FlagIdentityResolved;
                rec.VolumeSerial = e.VolumeSerial;
                rec.FileIdLow = e.FileIdLow;
                rec.FileIdHigh = e.FileIdHigh;
                rec.AllocatedSize = e.AllocatedSize;
                rec.ChangeTicks = e.ChangeTicks;
            }
            _records.Add(rec);
            _c.FilesDiscovered++;
            _c.LogicalBytesDiscovered += e.Size;
        };

        foreach (var root in NormalizeRoots())
        {
            _ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                _errors.Add(root, FileStatus.FileNotFoundDuringScan, "Папка не найдена");
                Interlocked.Increment(ref _c.ErrorFiles);
                continue;
            }
            int domain = _domains.GetOrAdd(_fs.GetStorageProfile(root));
            _volumeProbePaths.Add(root);
            _inventory?.BeginRoot(root, _ct);
            if (_opt.FollowDirectoryReparsePoints)
            {
                if (!_fs.TryGetDirectoryIdentity(root, out var rid))
                {
                    _errors.Add(root, FileStatus.Unsupported, "Без identity корневой папки безопасный обход ссылок невозможен");
                    Interlocked.Increment(ref _c.ErrorFiles);
                    continue;
                }
                if (!visited.Add(rid)) continue;
            }
            stack.Push(_paths.AddDirectory(root, domain));

            while (stack.Count > 0)
            {
                _ct.ThrowIfCancellationRequested();
                curDir = stack.Pop();
                curPath = _paths.GetDirectory(curDir);
                curDomain = _paths.GetDirectoryDomain(curDir);
                string? err;
                var st = _inventory != null ? _inventory.Enumerate(curPath, handler, _ct, out err)
                    : _fs.EnumerateDirectory(curPath, handler, _ct, out err);
                Interlocked.Increment(ref _c.DirectoriesScanned);
                if (st != FileStatus.Ok)
                {
                    _errors.Add(curPath, st, "Не удалось прочитать папку: " + err);
                    Interlocked.Increment(ref _c.ErrorFiles);
                }
            }
        }
    }

    private void CopyEnumerationStats()
    {
        if (_fs is IEnumerationStats st)
        {
            _c.FastEnumeratedDirectories = st.FastDirectories;
            _c.FallbackEnumeratedDirectories = st.FallbackDirectories;
        }
    }

    // ------------------------------------------------------------------ Size grouping (ТЗ §6)

    private (List<int[]> Groups, List<int> Zero) GroupBySize()
    {
        int n = _records.Count;
        var sizes = new long[n];
        var idx = new int[n];
        for (int i = 0; i < n; i++) { sizes[i] = _records[i].Size; idx[i] = i; }
        Array.Sort(sizes, idx);

        var groups = new List<int[]>();
        var zero = new List<int>();
        int start = 0;
        while (start < n)
        {
            _ct.ThrowIfCancellationRequested();
            int end = start + 1;
            while (end < n && sizes[end] == sizes[start]) end++;
            int count = end - start;
            if (sizes[start] == 0)
            {
                zero.AddRange(idx.AsSpan(start, count).ToArray());
                _c.ZeroByteFiles = count;
            }
            else if (count == 1)
            {
                _c.UniqueSizeFilesRejected++;   // no content I/O at all for this file
            }
            else
            {
                var g = idx.AsSpan(start, count).ToArray();
                Array.Sort(g);               // enumeration order ≈ on-disk locality
                groups.Add(g);
            }
            start = end;
        }
        zero.Sort();
        return (groups, zero);
    }

    // ------------------------------------------------------------------ Hard links (ТЗ §7)

    private int DomainOf(int rec) => _paths.GetDirectoryDomain(_records[rec].DirIndex);

    private List<int[]> ResolvePhysicalIdentity(List<int[]> groups)
    {
        var all = groups.SelectMany(g => g).OrderBy(i => i).ToList();
        _c.StageTotal = all.Count;
        _io.Run(all, DomainOf, _t.QuickReaders, 16, false, (rec, _, _) =>
        {
            string path = PathOf(rec);
            var st = _fs.GetIdentity(path, out var id);
            ref var r = ref _records[rec];
            if (st != FileStatus.Ok)
            {
                Fail(rec, st);
            }
            else if (id.Size >= 0 && id.Size != r.Size)
            {
                Fail(rec, FileStatus.ChangedDuringScan, "Размер изменился после обхода");
            }
            else
            {
                r.Flags |= FileRecord.FlagIdentityResolved;
                if (id.Valid)
                {
                    r.Flags |= FileRecord.FlagHasIdentity;
                    r.VolumeSerial = id.VolumeSerial;
                    r.FileIdLow = id.FileIdLow;
                    r.FileIdHigh = id.FileIdHigh;
                }
                r.LinkCount = id.LinkCount;
                r.AllocatedSize = id.AllocatedSize;
                r.ChangeTicks = id.ChangeTicks;
                r.CreationTicks = id.CreationTicks;
                if (id.LastWriteTicks != 0) r.LastWriteTicks = id.LastWriteTicks;
            }
            Interlocked.Increment(ref _c.StageDone);
        }, _ct);

        var result = new List<int[]>();
        var map = new Dictionary<(ulong, ulong, ulong), int>();
        var reps = new List<int>();
        foreach (var g in groups)
        {
            map.Clear();
            reps.Clear();
            foreach (int i in g)
            {
                ref var r = ref _records[i];
                if (r.Status != FileStatus.Ok) continue;
                if (r.HasIdentity)
                {
                    var key = (r.VolumeSerial, r.FileIdLow, r.FileIdHigh);
                    if (map.TryGetValue(key, out int rep))
                    {
                        r.PhysicalRep = rep;
                        _records[rep].AliasCount++;
                        if (!_aliases.TryGetValue(rep, out var list)) _aliases[rep] = list = new List<int>();
                        list.Add(i);
                        _c.HardlinkAliasesDetected++;
                        continue;
                    }
                    map[key] = i;
                }
                r.PhysicalRep = i;
                reps.Add(i);
            }
            if (reps.Count >= 2) result.Add(reps.ToArray());
        }
        return result;
    }

    // ------------------------------------------------------------------ Quick samples (ТЗ §9)

    private static ref ulong QSlot(ref FileRecord r, int stage) => ref r.QuickHash;

    private static bool MatchesVersion(in FileRecord r, in MetaSnapshot s) =>
        r.Size == s.Size && r.LastWriteTicks == s.LastWriteTicks &&
        r.ChangeTicks == s.ChangeTicks && r.CreationTicks == s.CreationTicks &&
        (!r.HasIdentity || (s.HasIdentity && r.VolumeSerial == s.VolumeSerial &&
            r.FileIdLow == s.FileIdLow && r.FileIdHigh == s.FileIdHigh));

    private bool ValidateVersion(int rec)
    {
        if (_records[rec].Status != FileStatus.Ok) return false;
        var st = _fs.GetIdentity(PathOf(rec), out var id);
        if (st != FileStatus.Ok) { Fail(rec, st); return false; }
        ref var r = ref _records[rec];
        if (id.Size != r.Size || id.LastWriteTicks != r.LastWriteTicks ||
            id.ChangeTicks != r.ChangeTicks || id.CreationTicks != r.CreationTicks ||
            (r.HasIdentity && (!id.Valid || id.VolumeSerial != r.VolumeSerial ||
                id.FileIdLow != r.FileIdLow || id.FileIdHigh != r.FileIdHigh)))
        {
            Fail(rec, FileStatus.ChangedDuringScan, "Файл изменился после проверки");
            return false;
        }
        return true;
    }

    /// <param name="offsetFor">Deterministic sample offset for a file size, or -1 when the stage does not apply.</param>
    private List<int[]> QuickStage(List<int[]> groups, int stage, string phase, Func<long, long> offsetFor)
    {
        var applicable = groups.Where(g => offsetFor(_records[g[0]].Size) >= 0).ToList();
        if (applicable.Count == 0) return groups;
        Phase(phase);

        var items = applicable.SelectMany(g => g).OrderBy(i => i).ToList();
        _c.StageTotal = items.Count;
        int sample = _t.SampleSize;
        byte bit = (byte)(1 << (stage - 1));
        var fromCache = items.Where(i => (_records[i].CachedQMask & bit) != 0).ToList();
        if (fromCache.Count > 0)
        {
            foreach (int rec in fromCache)
                if (ValidateVersion(rec)) _records[rec].QuickHash = _cachedQuick[rec].Q[stage - 1];
            items = items.Where(i => (_records[i].CachedQMask & bit) == 0).ToList();
            Interlocked.Add(ref _c.StageDone, fromCache.Count);
        }
        _io.Run(items, DomainOf, _t.QuickReaders, sample, false, (rec, buf, _) =>
        {
            string path = PathOf(rec);
            ref var r = ref _records[rec];
            try
            {
                using var h = _fs.OpenRead(path, sequential: false);
                if (!_fs.TryGetSnapshot(h, path, out var before)) { Fail(rec, FileStatus.Unsupported, "Метаданные для проверки изменений недоступны"); return; }
                if (!MatchesVersion(r, before)) { Fail(rec, FileStatus.ChangedDuringScan, "Размер изменился во время сканирования"); return; }
                long off = offsetFor(r.Size);
                int want = (int)Math.Min(sample, r.Size - off);
                int got = 0;
                while (got < want)
                {
                    _ct.ThrowIfCancellationRequested();
                    int n = RandomAccess.Read(h, buf.AsSpan(got, want - got), off + got);
                    Interlocked.Add(ref _c.QuickHashBytesRead, n);
                    if (n == 0) break;
                    got += n;
                }
                if (got != want) { Fail(rec, FileStatus.ChangedDuringScan, "Файл укоротился во время чтения"); return; }
                if (!_fs.TryGetSnapshot(h, path, out var after) || !after.Equals(before)) { Fail(rec, FileStatus.ChangedDuringScan, "Файл изменился во время выборки"); return; }
                QSlot(ref r, stage) = _quick.Hash(buf.AsSpan(0, got));
                Interlocked.Increment(ref _c.QuickHashFiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(rec, FileStatusText.FromException(ex), ex.Message);
            }
            finally
            {
                Interlocked.Increment(ref _c.StageDone);
            }
        }, _ct);

        StoreQuick(items, stage);

        var result = groups.Where(g => offsetFor(_records[g[0]].Size) < 0).ToList();
        foreach (var g in applicable)
        {
            foreach (var sub in g.Where(i => _records[i].Status == FileStatus.Ok)
                                 .GroupBy(i => QSlot(ref _records[i], stage)))
            {
                var arr = sub.ToArray();
                if (arr.Length >= 2) result.Add(arr);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ Full hash (ТЗ §10, §12)

    private List<int[]> FullHashStage(List<int[]> groups)
    {
        var items = groups.SelectMany(g => g).Where(i => _records[i].Status == FileStatus.Ok).OrderBy(i => i).ToList();
        _fullHashes = new byte[items.Count * 32];
        for (int s = 0; s < items.Count; s++) _records[items[s]].FullHashSlot = s;
        foreach (int i in items.Where(i => _cachedFull.ContainsKey(i)))
            _cachedFull[i].CopyTo(_fullHashes.AsSpan(_records[i].FullHashSlot * 32, 32));
        items = items.Where(i => !_cachedFull.ContainsKey(i)).ToList();
        _c.StageTotal = items.Sum(i => _records[i].Size);

        var snapshots = new MetaSnapshot[_fullHashes.Length / 32];
        _io.Run(items, DomainOf, _t.FullReaders, _t.StreamBufferSize, false, (rec, buf, _) =>
        {
            string path = PathOf(rec);
            for (int attempt = 0; attempt < 2; attempt++)   // one automatic retry (ТЗ §12)
            {
                var outcome = HashOnce(rec, path, buf, out var msg, out snapshots[_records[rec].FullHashSlot]);
                if (outcome == FileStatus.Ok)
                {
                    Interlocked.Increment(ref _c.FullHashFiles);
                    return;
                }
                if (outcome != FileStatus.ChangedDuringScan || attempt == 1)
                {
                    Fail(rec, outcome, msg);
                    return;
                }
            }
        }, _ct, _t.FullReadersStart, i => _records[i].Size);
        StoreFull(items, snapshots);

        var result = new List<int[]>();
        foreach (var g in groups)
        {
            foreach (var sub in g.Where(i => _records[i].Status == FileStatus.Ok).GroupBy(HashKey))
            {
                var arr = sub.ToArray();
                if (arr.Length >= 2) result.Add(arr);
            }
        }
        return result;
    }

    private (ulong, ulong, ulong, ulong) HashKey(int rec)
    {
        var s = _fullHashes.AsSpan(_records[rec].FullHashSlot * 32, 32);
        return (BitConverter.ToUInt64(s), BitConverter.ToUInt64(s[8..]), BitConverter.ToUInt64(s[16..]), BitConverter.ToUInt64(s[24..]));
    }

    private FileStatus HashOnce(int rec, string path, byte[] buf, out string? message, out MetaSnapshot snapshot)
    {
        message = null;
        snapshot = default;
        long size = _records[rec].Size;
        long progressed = 0;
        try
        {
            using var h = _fs.OpenRead(path, sequential: true);
            if (!_fs.TryGetSnapshot(h, path, out var before)) { message = "Метаданные для проверки изменений недоступны"; return FileStatus.Unsupported; }
            if (!MatchesVersion(_records[rec], before)) { message = "Размер изменился во время сканирования"; return FileStatus.ChangedDuringScan; }

            using var state = _full.Create();
            long off = 0;
            int block = 0;
            int bufLen = Math.Min(buf.Length, _t.StreamBufferSize);
            while (off < size)
            {
                _ct.ThrowIfCancellationRequested();   // partial hashes are discarded, never stored
                int want = (int)Math.Min(bufLen, size - off);
                int n = RandomAccess.Read(h, buf.AsSpan(0, want), off);
                if (n == 0) { message = "Файл укоротился во время чтения"; return FileStatus.ChangedDuringScan; }
                state.Update(buf.AsSpan(0, n));
                off += n;
                progressed += n;
                Interlocked.Add(ref _c.FullHashBytesRead, n);
                Interlocked.Add(ref _c.StageDone, n);
                _opt.AfterFullHashBlock?.Invoke(path, block++);
            }

            if (!_fs.TryGetSnapshot(h, path, out var after) || !after.Equals(before))
            {
                message = "Файл изменился во время хеширования";
                Interlocked.Add(ref _c.StageDone, -progressed);
                return FileStatus.ChangedDuringScan;
            }

            state.Finalize(_fullHashes.AsSpan(_records[rec].FullHashSlot * 32, 32));
            snapshot = before;
            return FileStatus.Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            message = ex.Message;
            return FileStatusText.FromException(ex);
        }
    }

    // ------------------------------------------------------------------ Persistent cache (ТЗ §18)

    private void OpenCache()
    {
        try
        {
            _cache = new ScanCache(_opt.CachePath!, _t.SampleSize);
        }
        catch (Exception ex)
        {
            // The cache is an optimisation: without it the scan is just slower.
            _cache = null;
            _c.CacheNote = "Кеш недоступен: " + ex.Message;
        }
    }

    private CacheKey KeyOf(int rec, long lastWrite, long change)
    {
        ref var r = ref _records[rec];
        return new CacheKey
        {
            HasId = r.HasIdentity,
            VolumeSerial = r.VolumeSerial,
            FileIdLow = r.FileIdLow,
            FileIdHigh = r.FileIdHigh,
            Path = PathOf(rec),
            Size = r.Size,
            LastWriteTicks = lastWrite,
            ChangeTicks = change,
            CreationTicks = r.CreationTicks,
        };
    }

    private void LoadFromCache(List<int[]> groups)
    {
        var items = groups.SelectMany(g => g).ToList();
        _c.StageTotal = items.Count;
        foreach (int i in items)
        {
            _ct.ThrowIfCancellationRequested();
            ref var r = ref _records[i];
            if (!ValidateVersion(i)) continue;
            CacheEntry? e;
            bool hit;
            try { hit = _cache!.TryGet(KeyOf(i, r.LastWriteTicks, r.ChangeTicks), out e); }
            catch (Exception ex) { _c.CacheNote = "Ошибка чтения кеша: " + ex.Message; _cache!.Dispose(); _cache = null; return; }
            if (hit && e != null)
            {
                Interlocked.Increment(ref _c.CacheHits);
                _cachedQuick[i] = e;
                r.CachedQMask = e.QMask;
                if (e.FullHash != null)
                {
                    _cachedFull[i] = e.FullHash;
                    r.Flags |= FileRecord.FlagFullFromCache;
                }
            }
            else
            {
                Interlocked.Increment(ref _c.CacheMisses);
            }
            _c.StageDone++;
        }
    }

    private void StoreQuick(List<int> processed, int stage)
    {
        if (_cache == null) return;
        int idx = stage - 1;
        var batch = new List<(CacheKey, CacheEntry)>();
        foreach (int i in processed)
        {
            ref var r = ref _records[i];
            if (r.Status != FileStatus.Ok) continue;
            var e = new CacheEntry { QMask = (byte)(1 << idx) };
            e.Q[idx] = QSlot(ref r, stage);
            batch.Add((KeyOf(i, r.LastWriteTicks, r.ChangeTicks), e));
        }
        Write(batch);
    }

    private void StoreFull(List<int> processed, MetaSnapshot[] snapshots)
    {
        if (_cache == null) return;
        var batch = new List<(CacheKey, CacheEntry)>();
        foreach (int i in processed)
        {
            ref var r = ref _records[i];
            if (r.Status != FileStatus.Ok) continue;
            var snap = snapshots[r.FullHashSlot];
            // Key the hash by the metadata observed around the read itself (before == after).
            long mt = snap.LastWriteTicks != 0 ? snap.LastWriteTicks : r.LastWriteTicks;
            long ct = snap.ChangeTicks != 0 ? snap.ChangeTicks : r.ChangeTicks;
            var e = new CacheEntry { FullHash = _fullHashes.AsSpan(r.FullHashSlot * 32, 32).ToArray() };
            batch.Add((KeyOf(i, mt, ct), e));
        }
        Write(batch);
    }

    private void Write(List<(CacheKey, CacheEntry)> batch)
    {
        if (_cache == null || batch.Count == 0) return;
        try
        {
            _cache.PutMany(batch);
            Interlocked.Add(ref _c.CacheWrites, batch.Count);
        }
        catch (Exception ex)
        {
            _c.CacheNote = "Ошибка записи кеша: " + ex.Message;
            _cache.Dispose();
            _cache = null;
        }
    }

    // ------------------------------------------------------------------ USN Journal (ТЗ §19)

    /// <summary>
    /// For every scanned volume with a readable journal: drop cache entries of files changed
    /// since the last scan; on a journal reset or gap drop the whole volume. The journal is
    /// only ever used to invalidate, never as a content signature.
    /// </summary>
    private void ApplyUsnJournal()
    {
        if (_fs is not IUsnSource usn || _cache == null) return;
        var seen = new HashSet<ulong>();
        foreach (var probe in _volumeProbePaths)
        {
            _ct.ThrowIfCancellationRequested();
            if (!usn.TryGetVolume(probe, out var volRoot, out var vol) || !seen.Add(vol)) continue;
            try
            {
                bool hadState = _cache.TryGetUsnState(vol, out var oldJournal, out var oldNext);
                if (!usn.TryQueryJournal(volRoot, out var journal, out var next, out var lowest))
                {
                    _c.UsnUnavailable++;
                    if (hadState) _c.UsnInvalidated += _cache.InvalidateVolume(vol);
                    continue;
                }
                if (hadState)
                {
                    if (oldJournal != journal || oldNext < lowest || oldNext > next)
                    {
                        _c.UsnInvalidated += _cache.InvalidateVolume(vol);
                    }
                    else if (oldNext < next)
                    {
                        var changed = new HashSet<(ulong, ulong)>();
                        if (usn.TryReadChanges(volRoot, journal, oldNext, next, changed, _ct))
                            _c.UsnInvalidated += _cache.InvalidateIds(vol, changed);
                        else
                        {
                            _c.UsnUnavailable++;
                            _c.UsnInvalidated += _cache.InvalidateVolume(vol);
                            continue;
                        }
                    }
                    _c.UsnVolumesTracked++;
                }
                // Position captured before reading files: changes during this scan show up next time.
                _usnToSave.Add((vol, journal, next));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _c.CacheNote = "USN Journal: " + ex.Message;
            }
        }
    }

    private void SaveUsnState()
    {
        if (_cache == null) return;
        try
        {
            foreach (var (vol, journal, next) in _usnToSave) _cache.SetUsnState(vol, journal, next);
        }
        catch (Exception ex) { _c.CacheNote = "USN Journal: " + ex.Message; }
    }

    // ------------------------------------------------------------------ Alternate data streams (ТЗ §2)

    /// <summary>
    /// Splits HASH_MATCH groups by a signature of the named streams: sorted names, sizes and the
    /// BLAKE3 of each stream's content. Streams are read with the same streaming buffer.
    /// </summary>
    private List<int[]> AlternateStreamStage(List<int[]> groups)
    {
        var items = groups.SelectMany(g => g).OrderBy(i => i).ToList();
        var sigs = new Dictionary<int, (ulong, ulong, ulong, ulong)>();
        var gate = new object();
        _c.StageTotal = items.Count;
        _io.Run(items, DomainOf, _t.FullReaders, _t.StreamBufferSize, false, (rec, buf, _) =>
        {
            string path = PathOf(rec);
            try
            {
                using var primary = _fs.OpenRead(path, sequential: false);
                if (!_fs.TryGetSnapshot(primary, path, out var primaryBefore)) { Fail(rec, FileStatus.Unsupported, "Метаданные для ADS недоступны"); return; }
                if (!MatchesVersion(_records[rec], primaryBefore)) { Fail(rec, FileStatus.ChangedDuringScan); return; }
                var streams = new List<(string Name, long Size)>();
                var st = _fs.ListAlternateStreams(path, streams);
                if (st != FileStatus.Ok) { Fail(rec, st, "Не удалось перечислить альтернативные потоки"); return; }
                streams.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                using var sig = _full.Create();
                Span<byte> tmp = stackalloc byte[32];
                BitConverter.TryWriteBytes(tmp, streams.Count);
                sig.Update(tmp[..4]);
                foreach (var (name, size) in streams)
                {
                    sig.Update(System.Text.Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
                    BitConverter.TryWriteBytes(tmp, size);
                    sig.Update(tmp[..8]);
                    using var h = _fs.OpenAlternateStream(path, name);
                    using var content = _full.Create();
                    long off = 0;
                    while (off < size)
                    {
                        _ct.ThrowIfCancellationRequested();
                        int n = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(buf.Length, size - off)), off);
                        if (n == 0) break;
                        content.Update(buf.AsSpan(0, n));
                        off += n;
                        Interlocked.Add(ref _c.AlternateStreamBytesRead, n);
                    }
                    if (off != size) { Fail(rec, FileStatus.ChangedDuringScan, "Альтернативный поток изменился во время чтения"); return; }
                    content.Finalize(tmp);
                    sig.Update(tmp);
                }
                var streamsAfter = new List<(string Name, long Size)>();
                var afterStatus = _fs.ListAlternateStreams(path, streamsAfter);
                streamsAfter.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (afterStatus != FileStatus.Ok || !streams.SequenceEqual(streamsAfter) ||
                    !_fs.TryGetSnapshot(primary, path, out var primaryAfter) || !primaryAfter.Equals(primaryBefore))
                { Fail(rec, FileStatus.ChangedDuringScan, "Альтернативные потоки изменились"); return; }
                sig.Finalize(tmp);
                var key = (BitConverter.ToUInt64(tmp), BitConverter.ToUInt64(tmp[8..]), BitConverter.ToUInt64(tmp[16..]), BitConverter.ToUInt64(tmp[24..]));
                lock (gate) sigs[rec] = key;
                Interlocked.Increment(ref _c.AlternateStreamFiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(rec, FileStatusText.FromException(ex), ex.Message);
            }
            finally
            {
                Interlocked.Increment(ref _c.StageDone);
            }
        }, _ct);

        var result = new List<int[]>();
        foreach (var g in groups)
            foreach (var sub in g.Where(i => _records[i].Status == FileStatus.Ok && sigs.ContainsKey(i)).GroupBy(i => sigs[i]))
            {
                var arr = sub.ToArray();
                if (arr.Length >= 2) result.Add(arr);
            }
        return result;
    }

    // ------------------------------------------------------------------ ExactVerifier (ТЗ §11)

    private List<int[]> ExactVerify(List<int[]> groups)
    {
        var verified = new List<int[]>();
        var gate = new object();
        _c.StageTotal = groups.Sum(g => _records[g[0]].Size * g.Length);
        var order = Enumerable.Range(0, groups.Count).ToList();
        int bufSize = Math.Max(_t.StreamBufferSize, 4 << 20);
        _io.Run(order, gi => DomainOf(groups[gi][0]), _t.FullReaders, bufSize, true, (gi, b1, b2) =>
        {
            var remaining = groups[gi].ToList();
            while (remaining.Count >= 2)
            {
                int reference = remaining[0];
                var same = new List<int> { reference };
                var differ = new List<int>();
                bool refFailed = false;
                foreach (int other in remaining.Skip(1))
                {
                    var r = CompareFiles(reference, other, b1, b2, out var failedRec, out var msg);
                    if (r == CompareResult.Equal) same.Add(other);
                    else if (r == CompareResult.Different) differ.Add(other);
                    else
                    {
                        Fail(failedRec, _records[failedRec].Status, msg);
                        if (failedRec == reference) { refFailed = true; break; }
                    }
                }
                if (refFailed)
                {
                    remaining = remaining.Skip(1).Where(i => _records[i].Status == FileStatus.Ok).ToList();
                    continue;
                }
                if (same.Count >= 2)
                    lock (gate) verified.Add(same.ToArray());
                remaining = differ;  // equal BLAKE3 but different bytes: verify the rest among themselves
            }
        }, _ct);
        return verified;
    }

    private enum CompareResult { Equal, Different, Failed }

    private CompareResult CompareFiles(int a, int b, byte[] ba, byte[] bb, out int failedRec, out string? message)
    {
        failedRec = -1;
        message = null;
        long size = _records[a].Size;
        string pa = PathOf(a), pb = PathOf(b);
        Microsoft.Win32.SafeHandles.SafeFileHandle? ha = null, hb = null;
        try
        {
            try { ha = _fs.OpenRead(pa, true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failedRec = a; _records[a].Status = FileStatusText.FromException(ex); message = ex.Message; return CompareResult.Failed; }
            try { hb = _fs.OpenRead(pb, true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failedRec = b; _records[b].Status = FileStatusText.FromException(ex); message = ex.Message; return CompareResult.Failed; }

            bool snapA = _fs.TryGetSnapshot(ha, pa, out var sa0);
            bool snapB = _fs.TryGetSnapshot(hb, pb, out var sb0);
            if (!snapA || !MatchesVersion(_records[a], sa0)) { failedRec = a; _records[a].Status = snapA ? FileStatus.ChangedDuringScan : FileStatus.Unsupported; message = "Версия файла для сравнения изменилась или недоступна"; return CompareResult.Failed; }
            if (!snapB || !MatchesVersion(_records[b], sb0)) { failedRec = b; _records[b].Status = snapB ? FileStatus.ChangedDuringScan : FileStatus.Unsupported; message = "Версия файла для сравнения изменилась или недоступна"; return CompareResult.Failed; }
            long off = 0;
            int bufLen = Math.Min(ba.Length, bb.Length);
            while (off < size)
            {
                _ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(bufLen, size - off);
                int na = ReadFull(ha, ba.AsSpan(0, want), off);
                int nb = ReadFull(hb, bb.AsSpan(0, want), off);
                Interlocked.Add(ref _c.ExactCompareBytesRead, na + nb);
                Interlocked.Add(ref _c.StageDone, na);
                if (na != want) { failedRec = a; _records[a].Status = FileStatus.ChangedDuringScan; message = "Файл укоротился"; return CompareResult.Failed; }
                if (nb != want) { failedRec = b; _records[b].Status = FileStatus.ChangedDuringScan; message = "Файл укоротился"; return CompareResult.Failed; }
                if (!ba.AsSpan(0, want).SequenceEqual(bb.AsSpan(0, want)))
                    return CompareResult.Different;   // stop at the first differing block
                off += want;
            }
            if (_opt.CompareAlternateStreams)
            {
                var streamsA = new List<(string Name, long Size)>();
                var streamsB = new List<(string Name, long Size)>();
                var statusA = _fs.ListAlternateStreams(pa, streamsA);
                var statusB = _fs.ListAlternateStreams(pb, streamsB);
                if (statusA != FileStatus.Ok || statusB != FileStatus.Ok)
                {
                    failedRec = statusA != FileStatus.Ok ? a : b;
                    _records[failedRec].Status = statusA != FileStatus.Ok ? statusA : statusB;
                    message = "Альтернативные потоки для точной проверки недоступны";
                    return CompareResult.Failed;
                }
                streamsA.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name));
                streamsB.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name));
                if (streamsA.Count != streamsB.Count) return CompareResult.Different;
                for (int i = 0; i < streamsA.Count; i++)
                {
                    var streamA = streamsA[i]; var streamB = streamsB[i];
                    if (streamA.Size != streamB.Size || !StringComparer.OrdinalIgnoreCase.Equals(streamA.Name, streamB.Name))
                        return CompareResult.Different;
                    using var ah = _fs.OpenAlternateStream(pa, streamA.Name);
                    using var bh = _fs.OpenAlternateStream(pb, streamB.Name);
                    bool aSnapshot = _fs.TryGetSnapshot(ah, pa, out var aBefore);
                    bool bSnapshot = _fs.TryGetSnapshot(bh, pb, out var bBefore);
                    if (!aSnapshot || !bSnapshot || aBefore.Size != streamA.Size || bBefore.Size != streamB.Size)
                    {
                        failedRec = !aSnapshot || aBefore.Size != streamA.Size ? a : b;
                        _records[failedRec].Status = !aSnapshot || !bSnapshot ? FileStatus.Unsupported : FileStatus.ChangedDuringScan;
                        message = "Версия альтернативного потока недоступна или изменилась";
                        return CompareResult.Failed;
                    }
                    for (long position = 0; position < streamA.Size;)
                    {
                        _ct.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(bufLen, streamA.Size - position);
                        int na = ReadFull(ah, ba.AsSpan(0, want), position), nb = ReadFull(bh, bb.AsSpan(0, want), position);
                        Interlocked.Add(ref _c.ExactCompareBytesRead, na + nb);
                        if (na != want || nb != want)
                        {
                            failedRec = na != want ? a : b; _records[failedRec].Status = FileStatus.ChangedDuringScan;
                            message = "Альтернативный поток укоротился"; return CompareResult.Failed;
                        }
                        if (!ba.AsSpan(0, want).SequenceEqual(bb.AsSpan(0, want))) return CompareResult.Different;
                        position += want;
                    }
                    if (!_fs.TryGetSnapshot(ah, pa, out var aAfter) || !aAfter.Equals(aBefore))
                    { failedRec = a; _records[a].Status = FileStatus.ChangedDuringScan; return CompareResult.Failed; }
                    if (!_fs.TryGetSnapshot(bh, pb, out var bAfter) || !bAfter.Equals(bBefore))
                    { failedRec = b; _records[b].Status = FileStatus.ChangedDuringScan; return CompareResult.Failed; }
                }
            }
            if (snapA && (!_fs.TryGetSnapshot(ha, pa, out var sa1) || !sa1.Equals(sa0))) { failedRec = a; _records[a].Status = FileStatus.ChangedDuringScan; message = "Файл изменился во время сравнения"; return CompareResult.Failed; }
            if (snapB && (!_fs.TryGetSnapshot(hb, pb, out var sb1) || !sb1.Equals(sb0))) { failedRec = b; _records[b].Status = FileStatus.ChangedDuringScan; message = "Файл изменился во время сравнения"; return CompareResult.Failed; }
            return CompareResult.Equal;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failedRec = b;
            _records[b].Status = FileStatusText.FromException(ex);
            message = ex.Message;
            return CompareResult.Failed;
        }
        finally
        {
            ha?.Dispose();
            hb?.Dispose();
        }
    }

    private static int ReadFull(Microsoft.Win32.SafeHandles.SafeFileHandle h, Span<byte> buf, long off)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = RandomAccess.Read(h, buf[got..], off + got);
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    // ------------------------------------------------------------------ Results (ТЗ §23, §24)

    private DuplicateFile ResultFile(int m)
    {
        ref var r = ref _records[m];
        return new DuplicateFile
        {
            Path = PathOf(m),
            LogicalSize = r.Size,
            AllocatedSize = r.AllocatedSize,
            LastWriteUtc = DateTime.FromFileTimeUtc(Math.Max(0, r.LastWriteTicks)),
            LinkCount = r.LinkCount,
            HardLinkAliases = _aliases.TryGetValue(m, out var al) ? al.Select(PathOf).ToArray() : Array.Empty<string>(),
            Status = r.Status,
            ScannedVersion = new MetaSnapshot { Size = r.Size, LastWriteTicks = r.LastWriteTicks,
                ChangeTicks = r.ChangeTicks, CreationTicks = r.CreationTicks, VolumeSerial = r.VolumeSerial,
                FileIdLow = r.FileIdLow, FileIdHigh = r.FileIdHigh },
            PhysicalIdentity = r.HasIdentity ? (r.VolumeSerial, r.FileIdLow, r.FileIdHigh) : null,
        };
    }

    private ScanResult BuildResult(List<(int[] Members, VerificationState State)> groups, List<int> zero, TimeSpan elapsed)
    {
        var list = new List<DuplicateGroup>();
        foreach (var (members, state) in groups)
        {
            var files = members.Select(ResultFile).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

            list.Add(new DuplicateGroup
            {
                LogicalSize = _records[members[0]].Size,
                Hash = Convert.ToHexString(_fullHashes, _records[members[0]].FullHashSlot * 32, 32).ToLowerInvariant(),
                Verification = state,
                Files = files,
            });
        }
        list = list.OrderByDescending(g => g.ReclaimableLogicalBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
        var numbered = list.Select((g, i) => new DuplicateGroup
        {
            GroupId = i + 1, LogicalSize = g.LogicalSize, Hash = g.Hash, Verification = g.Verification, Files = g.Files,
        }).ToList();

        return new ScanResult
        {
            Groups = numbered,
            ZeroByteFiles = zero.Where(i => _records[i].Status == FileStatus.Ok).Select(PathOf).ToList(),
            ZeroByteGroups = _zeroGroups.Select((g, i) => new DuplicateGroup
            {
                GroupId = i + 1, LogicalSize = 0,
                Hash = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
                Verification = VerificationState.ExactMatch, Files = g.Select(ResultFile).ToArray(),
            }).ToArray(),
            Counters = _c.Snapshot(),
            Issues = _errors.Issues,
            IssueCounts = _errors.Counts,
            Storage = _domains.All,
            Elapsed = elapsed,
            PhaseTimes = _phaseTimes.ToArray(),
        };
    }
}
