using Microsoft.Win32.SafeHandles;

namespace FindCopy.Core;

public static class FileAttr
{
    public const uint ReadOnly = 0x1;
    public const uint Hidden = 0x2;
    public const uint System = 0x4;
    public const uint Directory = 0x10;
    public const uint Archive = 0x20;
    public const uint Device = 0x40;
    public const uint Normal = 0x80;
    public const uint Temporary = 0x100;
    public const uint SparseFile = 0x200;
    public const uint ReparsePoint = 0x400;
    public const uint Compressed = 0x800;
    public const uint Offline = 0x1000;
    public const uint Encrypted = 0x4000;
    public const uint RecallOnOpen = 0x40000;
    public const uint RecallOnDataAccess = 0x400000;

    public const uint NotLocalMask = Offline | RecallOnOpen | RecallOnDataAccess;
}

public static class ReparseTags
{
    public const uint MountPoint = 0xA0000003;   // junctions and volume mount points
    public const uint Symlink = 0xA000000C;
    public const uint LxSymlink = 0xA000001D;
    public const uint AppExecLink = 0x8000001B;
    public const uint Dedup = 0x80000013;
    public const uint CloudMask = 0xFFFF0FFF;    // IO_REPARSE_TAG_CLOUD_x share 0x9000x01A
    public const uint Cloud = 0x9000001A;

    /// <summary>Tags that are links to some other object: never treated as a separate copy.</summary>
    public static bool IsNameSurrogateLink(uint tag) =>
        tag is MountPoint or Symlink or LxSymlink or AppExecLink || (tag & 0x20000000) != 0;

    public static bool IsCloud(uint tag) => (tag & CloudMask) == Cloud;
}

public struct EntryInfo
{
    public bool IsDirectory;
    public uint Attributes;
    public uint ReparseTag;
    public long Size;
    public long LastWriteTicks;

    // Filled only by backends that return identity in bulk (FileIdExtdDirectoryInfo).
    public bool HasIdentity;
    public ulong VolumeSerial;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public long AllocatedSize;   // valid when HasIdentity
    public long ChangeTicks;     // valid when HasIdentity
}

public enum EnumerationBackend
{
    /// <summary>FileIdExtdDirectoryInfo where the file system supports it, otherwise FindFirstFileExW.</summary>
    Auto,
    /// <summary>Always FindFirstFileExW.</summary>
    Win32Only,
}

/// <summary>Which enumeration backend actually served the directories.</summary>
public interface IEnumerationStats
{
    long FastDirectories { get; }
    long FallbackDirectories { get; }
}

public delegate void DirEntryHandler(ReadOnlySpan<char> name, in EntryInfo info);

/// <summary>Physical identity of a file object plus allocation info (ТЗ §7, §15).</summary>
public struct FileIdentity
{
    public bool Valid;           // false: FileId unavailable, hard-link detection is disabled for this file
    public ulong VolumeSerial;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public uint LinkCount;
    public long AllocatedSize;   // -1 unknown
    public long Size;
    public long LastWriteTicks;  // 0 unknown
    public long ChangeTicks;     // 0 unknown
    public long CreationTicks;
}

/// <summary>Metadata snapshot used to detect a file changing under us (ТЗ §12).</summary>
public struct MetaSnapshot : IEquatable<MetaSnapshot>
{
    public long Size;
    public long LastWriteTicks;
    public long ChangeTicks;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public ulong VolumeSerial;
    public long CreationTicks;
    public long AllocatedSize;
    public uint LinkCount;

    public readonly bool HasIdentity => VolumeSerial != 0 && (FileIdLow != 0 || FileIdHigh != 0) && !(FileIdLow == ulong.MaxValue && FileIdHigh == ulong.MaxValue);

    public readonly bool Equals(MetaSnapshot o) =>
        Size == o.Size && LastWriteTicks == o.LastWriteTicks && ChangeTicks == o.ChangeTicks &&
        CreationTicks == o.CreationTicks && VolumeSerial == o.VolumeSerial &&
        FileIdLow == o.FileIdLow && FileIdHigh == o.FileIdHigh;

    public override readonly bool Equals(object? obj) => obj is MetaSnapshot m && Equals(m);
    public override readonly int GetHashCode() => HashCode.Combine(Size, LastWriteTicks, ChangeTicks, FileIdLow);
}

public readonly record struct StorageProfile(string DomainKey, StorageKind Kind, string Description);

/// <summary>
/// Platform layer. The scanner core only talks to this interface, so the Win32 backend can be
/// swapped (e.g. FileIdExtdDirectoryInfo enumeration) and the core can be tested elsewhere.
/// </summary>
public interface IFileSystem
{
    string NormalizeRoot(string root);
    FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error);
    FileStatus GetIdentity(string path, out FileIdentity identity);
    bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id);
    SafeFileHandle OpenRead(string path, bool sequential);
    bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot snapshot);
    StorageProfile GetStorageProfile(string path);

    /// <summary>
    /// Named data streams of a file, excluding the main unnamed stream (names like ":name:$DATA").
    /// File systems without streams report none.
    /// </summary>
    FileStatus ListAlternateStreams(string path, List<(string Name, long Size)> streams) => FileStatus.Ok;

    SafeFileHandle OpenAlternateStream(string path, string streamName) => OpenRead(path + streamName, true);
}

/// <summary>
/// Optional NTFS USN Journal access. The ordinary scan remains the fallback.
/// </summary>
public interface IUsnSource
{
    /// <summary>Volume mount root ("C:\") and the volume serial used in file identities.</summary>
    bool TryGetVolume(string path, out string volumeRoot, out ulong volumeSerial);
    bool TryQueryJournal(string volumeRoot, out ulong journalId, out long nextUsn, out long lowestValidUsn);
    /// <summary>Collects ids of files changed in [fromUsn, toUsn). False when the journal cannot be read.</summary>
    bool TryReadChanges(string volumeRoot, ulong journalId, long fromUsn, long toUsn, HashSet<(ulong Lo, ulong Hi)> changed, CancellationToken ct);
}

/// <summary>Journal identities and parents required to refresh a persisted directory inventory.</summary>
public readonly record struct UsnChange(ulong FileLow, ulong FileHigh, ulong ParentLow, ulong ParentHigh);

public interface IUsnInventorySource : IUsnSource
{
    /// <summary>False for incomplete intervals or records whose parents cannot be interpreted.</summary>
    bool TryReadDirectoryChanges(string volumeRoot, ulong journalId, long fromUsn, long toUsn,
        List<UsnChange> changes, CancellationToken ct);
}

public abstract class FileSystemBase : IFileSystem
{
    public virtual string NormalizeRoot(string root)
    {
        string full = Path.GetFullPath(root.Trim().Trim('"'));
        string? r = Path.GetPathRoot(full);
        if (full.Length > 1 && full != r)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    public abstract FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error);
    public abstract FileStatus GetIdentity(string path, out FileIdentity identity);
    public abstract bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id);
    public abstract bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot snapshot);
    public abstract StorageProfile GetStorageProfile(string path);

    public virtual FileStatus ListAlternateStreams(string path, List<(string Name, long Size)> streams) => FileStatus.Ok;

    public virtual SafeFileHandle OpenAlternateStream(string path, string streamName) => OpenRead(path + streamName, true);

    public virtual SafeFileHandle OpenRead(string path, bool sequential) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);

    public static IFileSystem CreateDefault() =>
        OperatingSystem.IsWindows() ? new WindowsFileSystem() : new PortableFileSystem();
}
