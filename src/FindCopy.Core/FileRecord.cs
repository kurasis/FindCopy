using System.Runtime.InteropServices;

namespace FindCopy.Core;

/// <summary>
/// Compact per-file record (ТЗ §5). Kept as a struct in one growable array; the path lives in
/// <see cref="PathStore"/> and is referenced by directory index + name handle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FileRecord
{
    public long NameHandle;      // offset into the name arena
    public int DirIndex;         // index into the directory table
    public ushort NameLength;
    public FileStatus Status;
    public byte Flags;
    public byte CachedQMask;     // bit (stage-1) set: the Q value of that stage came from the cache

    public long Size;            // logical size (EndOfFile)
    public long AllocatedSize;   // -1 when unknown
    public long LastWriteTicks;  // UTC FILETIME ticks
    public long ChangeTicks;     // metadata change time from the identity stage (0 = unknown)
    public uint Attributes;
    public uint ReparseTag;

    public ulong VolumeSerial;
    public ulong FileIdLow;
    public ulong FileIdHigh;
    public uint LinkCount;
    public int PhysicalRep;      // index of the representative record for this physical file, -1 = none
    public int AliasCount;       // number of extra hard-link paths that map to this representative

    // Regrouping preserves earlier stages; only the current fingerprint belongs in each record.
    public ulong QuickHash;
    public long CreationTicks;
    public int FullHashSlot;     // slot in the full hash table, -1 = none

    public const byte FlagHasIdentity = 1;
    public const byte FlagIdentityResolved = 2;
    public const byte FlagFullFromCache = 4;
    public const byte FlagHydrationRechecked = 8;

    public readonly bool HasIdentity => (Flags & FlagHasIdentity) != 0;
}

/// <summary>Growable array of structs with ref access (no per-item heap allocations).</summary>
public sealed class RecordList
{
    private FileRecord[] _items = new FileRecord[1024];
    public int Count { get; private set; }

    public ref FileRecord this[int index] => ref _items[index];

    public int Add(in FileRecord r)
    {
        if (Count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);
        _items[Count] = r;
        return Count++;
    }

    public Span<FileRecord> AsSpan() => _items.AsSpan(0, Count);
}

/// <summary>
/// Path storage (ТЗ §5): a directory table plus a chunked char arena for file names, so a
/// million files do not mean a million string objects.
/// </summary>
public sealed class PathStore
{
    private readonly int _chunkBits;
    private readonly int _chunkSize;
    private readonly List<char[]> _chunks = new();

    public PathStore(int chunkBits = 20)
    {
        if (chunkBits is < 9 or > 20) throw new ArgumentOutOfRangeException(nameof(chunkBits));
        _chunkBits = chunkBits;
        _chunkSize = 1 << chunkBits;
        _chunks.Add(new char[_chunkSize]);
    }
    private int _pos;
    private readonly List<string> _dirs = new();
    private readonly List<int> _dirDomains = new();

    public int AddDirectory(string fullPath, int domainId)
    {
        _dirs.Add(fullPath);
        _dirDomains.Add(domainId);
        return _dirs.Count - 1;
    }

    public string GetDirectory(int index) => _dirs[index];
    public int GetDirectoryDomain(int index) => _dirDomains[index];
    public int DirectoryCount => _dirs.Count;

    public long AddName(ReadOnlySpan<char> name)
    {
        if (name.Length > _chunkSize) throw new ArgumentException("name too long");
        if (_pos + name.Length > _chunkSize)
        {
            _chunks.Add(new char[_chunkSize]);
            _pos = 0;
        }
        int chunk = _chunks.Count - 1;
        name.CopyTo(_chunks[chunk].AsSpan(_pos));
        long handle = ((long)chunk << _chunkBits) | (uint)_pos;
        _pos += name.Length;
        return handle;
    }

    public ReadOnlySpan<char> GetName(long handle, int length)
    {
        int chunk = (int)(handle >> _chunkBits);
        int off = (int)(handle & (_chunkSize - 1));
        return _chunks[chunk].AsSpan(off, length);
    }

    public string GetFullPath(in FileRecord r) => CombinePath(_dirs[r.DirIndex], GetName(r.NameHandle, r.NameLength));

    public static string CombinePath(string dir, ReadOnlySpan<char> name)
    {
        char sep = Path.DirectorySeparatorChar;
        if (dir.Length > 0 && (dir[^1] == sep || dir[^1] == Path.AltDirectorySeparatorChar))
            return string.Concat(dir, name);
        return string.Concat(dir, new ReadOnlySpan<char>(in sep), name);
    }
}
