namespace FindCopy.Core;

/// <summary>One physical file object in a duplicate group, with its hard-link aliases (ТЗ §23).</summary>
public sealed class DuplicateFile
{
    public required string Path { get; init; }
    public long LogicalSize { get; init; }
    public long AllocatedSize { get; init; }           // -1 unknown
    public DateTime LastWriteUtc { get; init; }
    public uint LinkCount { get; init; }
    public IReadOnlyList<string> HardLinkAliases { get; init; } = Array.Empty<string>();
    public int HardLinkAliasCount => HardLinkAliases.Count;
    public FileStatus Status { get; init; }
    internal MetaSnapshot? ScannedVersion { get; init; }
    public long EstimatedReclaimableDiskBytes => LinkCount > 0 && LinkCount == 1 + HardLinkAliasCount ? Math.Max(0, AllocatedSize) : 0;
    internal (ulong Vol, ulong Lo, ulong Hi)? PhysicalIdentity { get; init; }
}

public sealed class DuplicateGroup
{
    public int GroupId { get; init; }
    public long LogicalSize { get; init; }
    public required string Hash { get; init; }
    public VerificationState Verification { get; init; }
    public required IReadOnlyList<DuplicateFile> Files { get; init; }

    /// <summary>All paths including hard-link aliases.</summary>
    public int FileCount => Files.Sum(f => 1 + f.HardLinkAliasCount);
    public int UniquePhysicalFileCount => Files.Count;

    /// <summary>logical_size × (unique_physical_files − 1) (ТЗ §24).</summary>
    public long ReclaimableLogicalBytes => LogicalSize * Math.Max(0, UniquePhysicalFileCount - 1);

    /// <summary>Allocation of eligible redundant objects, excluding objects retained by external links, ТЗ §15/§24.</summary>
    public long EstimatedReclaimableDiskBytes
    {
        get
        {
            if (Files.Count < 2 || LogicalSize == 0) return 0;
            long Reclaimable(DuplicateFile file) => file.EstimatedReclaimableDiskBytes;
            var estimates = Files.Select(Reclaimable).ToArray();
            return estimates.Sum() - estimates.Min();
        }
    }
}

public sealed class ScanResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }
    /// <summary>Zero-byte files: identical by definition, reclaimable = 0 (ТЗ §6).</summary>
    public IReadOnlyList<DuplicateGroup> ZeroByteGroups { get; init; } = Array.Empty<DuplicateGroup>();
    public required IReadOnlyList<string> ZeroByteFiles { get; init; }
    public required ScanCounters Counters { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required IReadOnlyDictionary<FileStatus, long> IssueCounts { get; init; }
    public required IReadOnlyList<StorageProfile> Storage { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<(string Phase, TimeSpan Duration)> PhaseTimes { get; init; } = Array.Empty<(string, TimeSpan)>();

    public long TotalReclaimableLogical => Groups.Sum(g => g.ReclaimableLogicalBytes);
    public long TotalReclaimableDisk => Groups.Sum(g => g.EstimatedReclaimableDiskBytes);

    /// <summary>True when some files could not be checked, so "no duplicates" must be qualified (ТЗ §20).</summary>
    public bool HasUncheckedFiles => Counters.SkippedDirectories > 0 || Counters.SkippedFiles > 0 || Counters.ErrorFiles > 0 || Counters.ChangedFiles > 0 || IssueCounts.Count > 0;
}
