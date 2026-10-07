using FindCopy.Core;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Bench;

/// <summary>Deterministic metadata workload; virtual entries explicitly do not measure native filesystem speed.</summary>
public sealed class MetadataFileSystem : FileSystemBase
{
    private const int PerDirectory = 10_000;
    private readonly string _root;
    private readonly int _count;
    public int ContentOpens;

    public MetadataFileSystem(string root, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        _root = Path.GetFullPath(root); _count = count;
        Directory.CreateDirectory(_root);
    }

    public override FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error)
    {
        error = null;
        if (dir == _root)
        {
            int directories = (_count + PerDirectory - 1) / PerDirectory;
            for (int i = 0; i < directories; i++)
            {
                ct.ThrowIfCancellationRequested();
                handler("d" + i, new EntryInfo { IsDirectory = true, Attributes = FileAttr.Directory });
            }
        }
        else
        {
            int directory = int.Parse(Path.GetFileName(dir).AsSpan(1));
            int end = Math.Min(_count, checked((directory + 1) * PerDirectory));
            for (int i = directory * PerDirectory; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                handler("f" + i + ".bin", new EntryInfo { Size = i + 1, LastWriteTicks = DateTime.UnixEpoch.ToFileTimeUtc() });
            }
        }
        return FileStatus.Ok;
    }
    public override FileStatus GetIdentity(string path, out FileIdentity id) { id = default; throw new InvalidOperationException("Unique-size metadata should not need file identities"); }
    public override bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id) { id = default; return false; }
    public override bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot snapshot) { snapshot = default; return false; }
    public override StorageProfile GetStorageProfile(string path) => new("metadata-fixture", StorageKind.Unknown, "Virtual metadata fixture; no physical device");
    public override SafeFileHandle OpenRead(string path, bool sequential) { Interlocked.Increment(ref ContentOpens); throw new InvalidOperationException("Metadata workload attempted content I/O"); }
}
