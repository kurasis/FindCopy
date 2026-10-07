namespace FindCopy.Core;

public sealed class ScanOptions
{
    /// <summary>One or more root folders.</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    /// <summary>Recursive = all subfolders; otherwise only files directly in each root.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Skip files and folders with the System attribute (default on).</summary>
    public bool SkipSystem { get; init; } = true;

    /// <summary>
    /// Folders never descended into while recursing (the root itself is always scanned when chosen
    /// explicitly). When null and <see cref="SkipSystem"/> is on, the Windows directory is excluded.
    /// </summary>
    public IReadOnlyList<string>? ExcludedDirectories { get; init; }

    /// <summary>Skip files and folders with the Hidden attribute.</summary>
    public bool SkipHidden { get; init; }

    /// <summary>Follow junctions / directory symlinks / mount points (ТЗ §13, default OFF).</summary>
    public bool FollowDirectoryReparsePoints { get; init; }

    /// <summary>Read online-only cloud files, which may download them (ТЗ §14, default OFF).</summary>
    public bool IncludeOnlineOnlyFiles { get; init; }

    /// <summary>Run the byte-for-byte ExactVerifier after hashing (ТЗ §11).</summary>
    public bool ExactVerification { get; init; }

    /// <summary>Persistent fingerprint cache file (ТЗ §18). Null = no cache.</summary>
    public string? CachePath { get; init; }

    /// <summary>Use the NTFS USN Journal to invalidate cache entries of changed files (ТЗ §19).</summary>
    public bool UseUsnJournal { get; init; } = true;

    /// <summary>
    /// "Compare alternate streams" mode (ТЗ §2): duplicates must also have the same set of NTFS named
    /// streams (names, sizes and content). Off by default: only the main stream is compared.
    /// </summary>
    public bool CompareAlternateStreams { get; init; }

    /// <summary>Directory enumeration backend on Windows (ТЗ §4).</summary>
    public EnumerationBackend EnumerationBackend { get; init; } = EnumerationBackend.Auto;

    public Tuning Tuning { get; init; } = Tuning.Default;

    /// <summary>Test hook: invoked after each streaming block of a full hash (path, block index).</summary>
    internal Action<string, int>? AfterFullHashBlock { get; init; }
}
