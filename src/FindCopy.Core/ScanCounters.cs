namespace FindCopy.Core;

/// <summary>Live counters (ТЗ §22). Updated with Interlocked by workers; polled by the UI.</summary>
public sealed class ScanCounters
{
    public long DirectoriesScanned;
    public long FilesDiscovered;
    public long LogicalBytesDiscovered;
    public long UniqueSizeFilesRejected;
    public long HardlinkAliasesDetected;
    public long QuickHashFiles;
    public long QuickHashBytesRead;
    public long FullHashFiles;
    public long FullHashBytesRead;
    public long ExactCompareBytesRead;
    public long AlternateStreamFiles;     // files whose named streams were compared
    public long AlternateStreamBytesRead;
    public long CacheHits;
    public long CacheMisses;
    public long CacheWrites;
    public long UsnVolumesTracked;     // volumes whose journal was read since the last scan
    public long UsnInvalidated;        // cache entries dropped because the journal reported a change
    public long UsnUnavailable;        // volumes without a readable journal (no rights, not NTFS, network)
    public long InventoryDirectoriesReused;
    public long InventoryEntriesReused;
    public long InventoryRootsRebuilt;
    public long HashMatchGroups;
    public long ExactMatchGroups;
    public long SkippedFiles;     // excluded by policy: reparse, cloud, system, hidden
    public long SkippedDirectories;
    public long SystemSkipped;    // subset of SkippedFiles, not itemised in the error list
    public long ErrorFiles;       // access denied, IO errors, vanished
    public long ChangedFiles;
    public long ZeroByteFiles;
    public long FastEnumeratedDirectories;      // served by FileIdExtdDirectoryInfo
    public long FallbackEnumeratedDirectories;  // served by FindFirstFileExW

    /// <summary>Stage work progress for the progress bar.</summary>
    public long StageTotal;
    public long StageDone;
    public volatile string Phase = "";
    public volatile string? CacheNote;
    public volatile string? AutotuneNote;

    public long ContentBytesRead =>
        Interlocked.Read(ref QuickHashBytesRead) + Interlocked.Read(ref FullHashBytesRead) +
        Interlocked.Read(ref ExactCompareBytesRead) + Interlocked.Read(ref AlternateStreamBytesRead);

    public double ReadAmplification
    {
        get
        {
            long total = Interlocked.Read(ref LogicalBytesDiscovered);
            return total == 0 ? 0 : (double)ContentBytesRead / total;
        }
    }

    public ScanCounters Snapshot() => (ScanCounters)MemberwiseClone();
}

public sealed record ScanIssue(string Path, FileStatus Status, string? Message);

/// <summary>Collects per-file problems without stopping the scan (ТЗ §20).</summary>
public sealed class ErrorCollector
{
    private readonly object _lock = new();
    private readonly List<ScanIssue> _issues = new();
    private readonly Dictionary<FileStatus, long> _counts = new();
    public int MaxStoredIssues { get; init; } = 100_000;

    public void Add(string path, FileStatus status, string? message = null)
    {
        lock (_lock)
        {
            _counts[status] = _counts.GetValueOrDefault(status) + 1;
            if (_issues.Count < MaxStoredIssues)
                _issues.Add(new ScanIssue(path, status, message));
        }
    }

    public IReadOnlyList<ScanIssue> Issues { get { lock (_lock) return _issues.ToArray(); } }
    public IReadOnlyDictionary<FileStatus, long> Counts { get { lock (_lock) return new Dictionary<FileStatus, long>(_counts); } }
    public long Count(FileStatus s) { lock (_lock) return _counts.GetValueOrDefault(s); }
}
