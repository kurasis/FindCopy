namespace FindCopy.Core;

/// <summary>Journal-assisted enumeration. Uncertain snapshots always use live directory enumeration.</summary>
internal sealed class DirectoryInventory
{
    private readonly IFileSystem _fs;
    private readonly IUsnInventorySource _journal;
    private readonly ScanCache _cache;
    private readonly ScanCounters _c;
    private readonly List<Root> _roots = new();
    private Root? _current;

    private sealed class Root
    {
        public required string Path;
        public required InventoryState State;
        public string? PreviousGeneration;
        public bool CanReuse;
        public bool Complete = true;
        public readonly HashSet<(ulong, ulong)> Dirty = new();
        public readonly HashSet<(ulong, ulong)> Changed = new();
    }

    public DirectoryInventory(IFileSystem fs, IUsnInventorySource journal, ScanCache cache, ScanCounters counters)
    {
        _fs = fs; _journal = journal; _cache = cache; _c = counters;
        _cache.BeginInventory();
    }

    public void BeginRoot(string root, CancellationToken ct)
    {
        _current = null;
        try
        {
            if (!_journal.TryGetVolume(root, out var volumeRoot, out var volume) ||
                !_fs.TryGetDirectoryIdentity(root, out var id) || id.Vol != volume ||
                !_journal.TryQueryJournal(volumeRoot, out var journal, out var next, out var lowest)) return;
            var old = _cache.GetInventoryState(root);
            var context = new Root
            {
                Path = root, PreviousGeneration = old?.Generation,
                State = new InventoryState(Guid.NewGuid().ToString("N"), volume, id.Lo, id.Hi, journal, next),
                CanReuse = old != null && old.Volume == volume && old.RootLow == id.Lo && old.RootHigh == id.Hi &&
                    old.Journal == journal && old.NextUsn >= lowest && old.NextUsn <= next,
            };
            if (context.CanReuse && old!.NextUsn < next)
            {
                var changes = new List<UsnChange>();
                context.CanReuse = _journal.TryReadDirectoryChanges(volumeRoot, journal, old.NextUsn, next, changes, ct);
                if (context.CanReuse)
                    foreach (var change in changes)
                    {
                        context.Dirty.Add((change.ParentLow, change.ParentHigh));
                        context.Dirty.Add((change.FileLow, change.FileHigh));
                        context.Changed.Add((change.FileLow, change.FileHigh));
                    }
            }
            if (!context.CanReuse) Interlocked.Increment(ref _c.InventoryRootsRebuilt);
            _current = context;
            _roots.Add(context);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _c.CacheNote = "Снимок каталогов недоступен: " + ex.Message; }
    }

    public FileStatus Enumerate(string path, DirEntryHandler handler, CancellationToken ct, out string? error)
    {
        var root = _current;
        if (root == null) return _fs.EnumerateDirectory(path, handler, ct, out error);
        (ulong Vol, ulong Lo, ulong Hi) id;
        byte[]? reused = null;
        int reusedCount = 0;
        try
        {
            if (!_fs.TryGetDirectoryIdentity(path, out id) || id.Vol != root.State.Volume)
            {
                root.Complete = false;
                return _fs.EnumerateDirectory(path, handler, ct, out error);
            }
            if (root.CanReuse && !root.Dirty.Contains((id.Lo, id.Hi)))
            {
                var bytes = _cache.GetInventoryDirectory(root.Path, root.PreviousGeneration!, path, id);
                if (bytes != null && DirectoryListing.IsReusable(bytes, root.Changed, ct, out int count))
                {
                    // Staging precedes replay so a database failure cannot duplicate emitted entries.
                    _cache.StageInventoryDirectory(root.Path, path, id, null);
                    reused = bytes;
                    reusedCount = count;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            root.Complete = false;
            _c.CacheNote = "Снимок каталогов: " + ex.Message;
            return _fs.EnumerateDirectory(path, handler, ct, out error);
        }

        if (reused != null)
        {
            // Handler failures are scan failures, not a reason to replay a partially emitted list.
            DirectoryListing.Replay(reused, handler, ct);
            Interlocked.Increment(ref _c.InventoryDirectoriesReused);
            Interlocked.Add(ref _c.InventoryEntriesReused, reusedCount);
            error = null;
            return FileStatus.Ok;
        }

        using var listing = new DirectoryListing();
        void Emit(ReadOnlySpan<char> name, in EntryInfo info)
        {
            listing.Append(name, info);
            handler(name, info);
        }
        var status = _fs.EnumerateDirectory(path, Emit, ct, out error);
        if (status != FileStatus.Ok) root.Complete = false;
        else
        {
            try
            {
                var bytes = listing.Finish();
                if (bytes == null) root.Complete = false;
                else _cache.StageInventoryDirectory(root.Path, path, id, bytes);
            }
            catch (Exception ex) { root.Complete = false; _c.CacheNote = "Снимок каталогов: " + ex.Message; }
        }
        return status;
    }

    public void Commit(CancellationToken ct)
    {
        foreach (var root in _roots.Where(r => r.Complete))
        {
            ct.ThrowIfCancellationRequested();
            try { _cache.CommitInventory(root.Path, root.State, root.PreviousGeneration); }
            catch (Exception ex) { _c.CacheNote = "Запись снимка каталогов: " + ex.Message; }
        }
    }
}

/// <summary>Bounded, versioned UTF-16 listings preserve names without per-entry heap objects.</summary>
internal sealed class DirectoryListing : IDisposable
{
    private const int Version = 2;
    private const int Limit = 256 << 20;
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    private int _count;
    private bool _overflow;

    public DirectoryListing()
    {
        _writer = new BinaryWriter(_stream);
        _writer.Write(Version); _writer.Write(0);
    }

    public void Append(ReadOnlySpan<char> name, in EntryInfo e)
    {
        if (_overflow) return;
        if (_stream.Length + 80 + name.Length * 2 > Limit || name.Length > ushort.MaxValue)
        { _overflow = true; return; }
        _writer.Write((ushort)name.Length);
        _writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name));
        _writer.Write(e.IsDirectory); _writer.Write(e.Attributes); _writer.Write(e.ReparseTag);
        _writer.Write(e.Size); _writer.Write(e.LastWriteTicks); _writer.Write(e.HasIdentity);
        _writer.Write(e.VolumeSerial); _writer.Write(e.FileIdLow); _writer.Write(e.FileIdHigh);
        _writer.Write(e.AllocatedSize); _writer.Write(e.ChangeTicks);
        _count++;
    }

    public byte[]? Finish()
    {
        if (_overflow) return null;
        _stream.Position = 4; _writer.Write(_count); _writer.Flush();
        _stream.Position = _stream.Length;
        ulong checksum = System.IO.Hashing.XxHash3.HashToUInt64(_stream.GetBuffer().AsSpan(0, (int)_stream.Length));
        _writer.Write(checksum); _writer.Flush();
        return _stream.ToArray();
    }

    private static ReadOnlySpan<char> ReadEntry(ReadOnlySpan<byte> bytes, ref int offset, out EntryInfo e)
    {
        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += 2;
        if (length == 0) throw new InvalidDataException("Empty inventory name");
        var name = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes.Slice(offset, length * 2));
        offset += length * 2;
        if (name.SequenceEqual(".") || name.SequenceEqual("..") || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
            throw new InvalidDataException("Invalid inventory name");
        var m = bytes.Slice(offset, 66);
        offset += 66;
        if (m[0] > 1 || m[25] > 1) throw new InvalidDataException("Invalid inventory flags");
        e = new EntryInfo
        {
            IsDirectory = m[0] != 0,
            Attributes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m[1..]),
            ReparseTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m[5..]),
            Size = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(m[9..]),
            LastWriteTicks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(m[17..]),
            HasIdentity = m[25] != 0,
            VolumeSerial = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(m[26..]),
            FileIdLow = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(m[34..]),
            FileIdHigh = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(m[42..]),
            AllocatedSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(m[50..]),
            ChangeTicks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(m[58..]),
        };
        if (e.Size < 0 || e.LastWriteTicks < 0) throw new InvalidDataException("Invalid inventory metadata");
        return name;
    }

    public static bool IsReusable(byte[] bytes, HashSet<(ulong, ulong)> changed, CancellationToken ct, out int count)
    {
        count = 0;
        if (!BitConverter.IsLittleEndian || bytes.Length > Limit || bytes.Length < 16 ||
            System.IO.Hashing.XxHash3.HashToUInt64(bytes.AsSpan(0, bytes.Length - 8)) !=
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8))) return false;
        try
        {
            var payload = bytes.AsSpan(0, bytes.Length - 8);
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload) != Version) return false;
            count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
            if (count < 0 || count > bytes.Length / 68) return false;
            int offset = 8;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReadEntry(payload, ref offset, out var e);
                // Every alias is checked: a write may produce a journal record for only one parent.
                if (!e.IsDirectory && (!e.HasIdentity || e.VolumeSerial == 0 ||
                    (e.FileIdLow == 0 && e.FileIdHigh == 0) || changed.Contains((e.FileIdLow, e.FileIdHigh)))) return false;
            }
            return offset == payload.Length;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException) { return false; }
    }

    public static void Replay(byte[] bytes, DirEntryHandler handler, CancellationToken ct)
    {
        var payload = bytes.AsSpan(0, bytes.Length - 8);
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int offset = 8;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = ReadEntry(payload, ref offset, out var info);
            handler(name, info);
        }
    }

    public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
}
