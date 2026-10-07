using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Core;

/// <summary>
/// System.IO backend for non-Windows hosts (used to run the engine test-suite on Linux).
/// On Linux x64 it uses stat(2) for dev/inode identity, link counts and ctime.
/// </summary>
public sealed unsafe class PortableFileSystem : FileSystemBase
{
    private static readonly long UnixFileTime = DateTime.UnixEpoch.ToFileTimeUtc();
    private static readonly bool UseStat = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public override FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error)
    {
        error = null;
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                bool isLink = fsi.LinkTarget != null;
                uint attrs = (uint)fsi.Attributes;
                if (isLink) attrs |= FileAttr.ReparsePoint;
                var info = new EntryInfo
                {
                    IsDirectory = fsi is DirectoryInfo,
                    Attributes = attrs,
                    ReparseTag = isLink ? ReparseTags.Symlink : 0,
                    Size = fsi is FileInfo fi && !isLink ? fi.Length : 0,
                    LastWriteTicks = fsi.LastWriteTimeUtc.ToFileTimeUtc(),
                };
                handler(fsi.Name, info);
            }
            return FileStatus.Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            return FileStatusText.FromException(ex);
        }
    }

    public override FileStatus GetIdentity(string path, out FileIdentity identity)
    {
        identity = new FileIdentity { AllocatedSize = -1, Size = -1 };
        if (UseStat)
        {
            byte* st = stackalloc byte[256];
            if (stat(path, st) != 0)
                return Errno(Marshal.GetLastWin32Error());
            identity.Valid = true;
            identity.VolumeSerial = *(ulong*)(st + 0);
            identity.FileIdLow = *(ulong*)(st + 8);
            identity.LinkCount = (uint)*(ulong*)(st + 16);
            identity.Size = *(long*)(st + 48);
            identity.AllocatedSize = *(long*)(st + 64) * 512;
            identity.LastWriteTicks = UnixFileTime + *(long*)(st + 88) * 10_000_000 + *(long*)(st + 96) / 100;
            identity.ChangeTicks = UnixFileTime + *(long*)(st + 104) * 10_000_000 + *(long*)(st + 112) / 100;
            return FileStatus.Ok;
        }
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return FileStatus.FileNotFoundDuringScan;
            identity.Size = fi.Length;
            return FileStatus.Ok;
        }
        catch (Exception ex) { return FileStatusText.FromException(ex); }
    }

    public override bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id)
    {
        id = default;
        if (!UseStat) return false;
        byte* st = stackalloc byte[256];
        if (stat(path, st) != 0) return false;
        id = (*(ulong*)st, *(ulong*)(st + 8), 0);
        return true;
    }

    public override bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot s)
    {
        s = default;
        if (UseStat)
        {
            byte* st = stackalloc byte[256];
            if (fstat((int)handle.DangerousGetHandle(), st) != 0) return false;
            s.VolumeSerial = *(ulong*)st;
            s.FileIdLow = *(ulong*)(st + 8);
            s.LinkCount = (uint)*(ulong*)(st + 16);
            s.AllocatedSize = *(long*)(st + 64) * 512;
            s.Size = *(long*)(st + 48);
            s.LastWriteTicks = UnixFileTime + *(long*)(st + 88) * 10_000_000 + *(long*)(st + 96) / 100;
            s.ChangeTicks = UnixFileTime + *(long*)(st + 104) * 10_000_000 + *(long*)(st + 112) / 100;
            return true;
        }
        try
        {
            s.Size = RandomAccess.GetLength(handle);
            s.LastWriteTicks = File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
            s.CreationTicks = File.GetCreationTimeUtc(path).ToFileTimeUtc();
            return true;
        }
        catch { return false; }
    }

    public override StorageProfile GetStorageProfile(string path)
    {
        if (UseStat)
        {
            byte* st = stackalloc byte[256];
            if (stat(path, st) == 0)
                return new StorageProfile("dev:" + *(ulong*)st, StorageKind.Unknown, "Неизвестный носитель");
        }
        return new StorageProfile("root:" + Path.GetPathRoot(path), StorageKind.Unknown, "Неизвестный носитель");
    }

    private static FileStatus Errno(int e) => e switch
    {
        2 or 20 => FileStatus.FileNotFoundDuringScan,
        1 or 13 => FileStatus.AccessDenied,
        _ => FileStatus.IoError,
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, byte* buf);
}
