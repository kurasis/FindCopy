using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Core;

/// <summary>
/// Win32 backend: Unicode APIs only, \\?\ extended paths (no MAX_PATH assumption),
/// FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH) enumeration (ТЗ §4).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsFileSystem : FileSystemBase, IUsnSource, IEnumerationStats
{
    private int _largeFetchSupported = 1;
    private readonly ConcurrentDictionary<string, bool> _extdUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private long _fastDirs, _fallbackDirs;

    public EnumerationBackend Backend { get; init; } = EnumerationBackend.Auto;
    public long FastDirectories => Interlocked.Read(ref _fastDirs);
    public long FallbackDirectories => Interlocked.Read(ref _fallbackDirs);
    private readonly ConcurrentDictionary<string, StorageProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    public override FileStatus EnumerateDirectory(string dir, DirEntryHandler handler, CancellationToken ct, out string? error)
    {
        if (Backend == EnumerationBackend.Auto)
        {
            string key = Path.GetPathRoot(dir) ?? dir;
            if (!_extdUnsupported.ContainsKey(key))
            {
                bool unsupported = false;
                FileStatus Fast(DirEntryHandler emit, out string? err)
                {
                    var status = EnumerateExtd(dir, emit, ct, out err, out unsupported);
                    return unsupported ? FileStatus.Unsupported : status;
                }
                FileStatus Slow(DirEntryHandler emit, out string? err) => EnumerateWin32(dir, emit, ct, out err);
                var st = DirectoryEnumerationFallback.Run(Fast, Slow, handler, ct, out bool fallback, out error);
                if (fallback) Interlocked.Increment(ref _fallbackDirs);
                else Interlocked.Increment(ref _fastDirs);
                if (unsupported) _extdUnsupported.TryAdd(key, true);
                return st;
            }
        }
        Interlocked.Increment(ref _fallbackDirs);
        return EnumerateWin32(dir, handler, ct, out error);
    }

    /// <summary>
    /// Optimised backend (ТЗ §4): GetFileInformationByHandleEx(FileIdExtdDirectoryInfo) returns names,
    /// sizes, allocation, attributes, reparse tags, timestamps and 128-bit FileIds in batches, so the
    /// scanner receives compact metadata in batches. Any unsupported/error result makes
    /// the caller retry with FindFirstFileExW, deduplicating entries already reported.
    /// </summary>
    private FileStatus EnumerateExtd(string dir, DirEntryHandler handler, CancellationToken ct, out string? error, out bool unsupported)
    {
        error = null;
        unsupported = false;
        using var h = Native.CreateFileW(ToExtendedPath(dir), Native.FILE_LIST_DIRECTORY | Native.SYNCHRONIZE,
            Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 5 or 2 or 3)
            {
                error = new System.ComponentModel.Win32Exception(err).Message;
                return FileStatusText.FromWin32(err);
            }
            unsupported = true;
            return FileStatus.Ok;
        }

        ulong volume = 0;
        Native.FILE_ID_INFO vid;
        if (Native.GetFileInformationByHandleEx(h, Native.FileIdInfo, &vid, (uint)sizeof(Native.FILE_ID_INFO)))
            volume = vid.VolumeSerialNumber;

        const int BufSize = 64 * 1024;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(BufSize, 8);
        try
        {
            bool first = true;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Native.GetFileInformationByHandleEx(h, first ? Native.FileIdExtdDirectoryRestartInfo : Native.FileIdExtdDirectoryInfo, buf, BufSize))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 18) return FileStatus.Ok;       // ERROR_NO_MORE_FILES
                    if (first && err is 1 or 50 or 87 or 124)   // invalid function / not supported / invalid parameter / invalid level
                    {
                        unsupported = true;
                        return FileStatus.Ok;
                    }
                    error = new System.ComponentModel.Win32Exception(err).Message;
                    return FileStatusText.FromWin32(err);
                }
                // Validate the whole batch before reporting anything from it.
                if (!BatchLooksValid(buf, BufSize))
                {
                    if (first) { unsupported = true; return FileStatus.Ok; }
                    error = "Некорректный ответ FileIdExtdDirectoryInfo";
                    return FileStatus.IoError;
                }
                first = false;
                uint off = 0;
                while (true)
                {
                    byte* e = buf + off;
                    uint next = *(uint*)e;
                    uint attrs = *(uint*)(e + 56);
                    int nameBytes = *(int*)(e + 60);
                    var name = new ReadOnlySpan<char>(e + 88, nameBytes / 2);
                    if (!(name is "." or ".."))
                    {
                        ulong lo = *(ulong*)(e + 72), hi = *(ulong*)(e + 80);
                        bool hasId = (lo != 0 || hi != 0) && !(lo == ulong.MaxValue && hi == ulong.MaxValue) && volume != 0;
                        var info = new EntryInfo
                        {
                            Attributes = attrs,
                            IsDirectory = (attrs & FileAttr.Directory) != 0,
                            ReparseTag = (attrs & FileAttr.ReparsePoint) != 0 ? *(uint*)(e + 68) : 0,
                            LastWriteTicks = *(long*)(e + 24),
                            Size = *(long*)(e + 40),
                            HasIdentity = hasId,
                            VolumeSerial = volume,
                            FileIdLow = lo,
                            FileIdHigh = hi,
                            AllocatedSize = *(long*)(e + 48),
                            ChangeTicks = *(long*)(e + 32),
                        };
                        handler(name, info);
                    }
                    if (next == 0) break;
                    off += next;
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    private static bool BatchLooksValid(byte* buf, int size)
    {
        uint off = 0;
        for (int guard = 0; guard < 100_000; guard++)
        {
            if (off + 88 > size) return false;
            byte* e = buf + off;
            uint next = *(uint*)e;
            int nameBytes = *(int*)(e + 60);
            if (nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > 255 * 2 * 2 || off + 88 + nameBytes > size) return false;
            if (next == 0) return true;
            if (next < 88 + nameBytes || (next & 7) != 0) return false;
            off += next;
        }
        return false;
    }

    private FileStatus EnumerateWin32(string dir, DirEntryHandler handler, CancellationToken ct, out string? error)
    {
        error = null;
        string pattern = ToExtendedPath(PathStore.CombinePath(dir, "*"));
        Native.WIN32_FIND_DATAW data;
        int flags = Volatile.Read(ref _largeFetchSupported) == 1 ? Native.FIND_FIRST_EX_LARGE_FETCH : 0;
        IntPtr h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out data, 0, IntPtr.Zero, flags);
        if (h == Native.INVALID_HANDLE_VALUE && flags != 0 && Marshal.GetLastWin32Error() == 87)
        {
            Volatile.Write(ref _largeFetchSupported, 0);
            h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out data, 0, IntPtr.Zero, 0);
        }
        if (h == Native.INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 2 or 18) return FileStatus.Ok; // empty
            error = new System.ComponentModel.Win32Exception(err).Message;
            return FileStatusText.FromWin32(err);
        }
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data.cFileName);
                if (name is "." or "..") continue;
                var info = new EntryInfo
                {
                    Attributes = data.dwFileAttributes,
                    IsDirectory = (data.dwFileAttributes & FileAttr.Directory) != 0,
                    ReparseTag = (data.dwFileAttributes & FileAttr.ReparsePoint) != 0 ? data.dwReserved0 : 0,
                    Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                    LastWriteTicks = data.ftLastWriteTime,
                };
                handler(name, info);
            }
            while (Native.FindNextFileW(h, out data));
            int last = Marshal.GetLastWin32Error();
            if (last != 18 /* ERROR_NO_MORE_FILES */)
            {
                error = new System.ComponentModel.Win32Exception(last).Message;
                return FileStatusText.FromWin32(last);
            }
        }
        finally
        {
            Native.FindClose(h);
        }
        return FileStatus.Ok;
    }

    private static SafeFileHandle OpenForAttributes(string path, out int error)
    {
        var h = Native.CreateFileW(ToExtendedPath(path), Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return h;
    }

    private static bool TryGetId(SafeFileHandle h, out ulong vol, out ulong lo, out ulong hi)
    {
        Native.FILE_ID_INFO idInfo;
        if (Native.GetFileInformationByHandleEx(h, Native.FileIdInfo, &idInfo, (uint)sizeof(Native.FILE_ID_INFO)))
        {
            vol = idInfo.VolumeSerialNumber;
            lo = idInfo.FileIdLow;
            hi = idInfo.FileIdHigh;
            if (vol != 0 && (lo != 0 || hi != 0) && !(lo == ulong.MaxValue && hi == ulong.MaxValue))
                return true;
        }
        Native.BY_HANDLE_FILE_INFORMATION bhi;
        if (Native.GetFileInformationByHandle(h, &bhi))
        {
            vol = bhi.dwVolumeSerialNumber;
            lo = ((ulong)bhi.nFileIndexHigh << 32) | bhi.nFileIndexLow;
            hi = 0;
            if (vol != 0 && lo != 0 && lo != ulong.MaxValue)
                return true;
        }
        vol = lo = hi = 0;
        return false;
    }

    public override FileStatus GetIdentity(string path, out FileIdentity identity)
    {
        identity = new FileIdentity { AllocatedSize = -1, Size = -1 };
        using var h = OpenForAttributes(path, out int err);
        if (h.IsInvalid) return FileStatusText.FromWin32(err);

        identity.Valid = TryGetId(h, out identity.VolumeSerial, out identity.FileIdLow, out identity.FileIdHigh);

        Native.FILE_STANDARD_INFO std;
        if (Native.GetFileInformationByHandleEx(h, Native.FileStandardInfo, &std, (uint)sizeof(Native.FILE_STANDARD_INFO)))
        {
            identity.LinkCount = std.NumberOfLinks;
            identity.AllocatedSize = std.AllocationSize;
            identity.Size = std.EndOfFile;
        }
        Native.FILE_BASIC_INFO basic;
        bool hasBasic = Native.GetFileInformationByHandleEx(h, Native.FileBasicInfo, &basic, (uint)sizeof(Native.FILE_BASIC_INFO));
        if (hasBasic)
        {
            identity.LastWriteTicks = basic.LastWriteTime;
            identity.ChangeTicks = basic.ChangeTime;
            identity.CreationTicks = basic.CreationTime;
        }
        if (hasBasic && (basic.FileAttributes & (FileAttr.Compressed | FileAttr.SparseFile)) != 0)
        {
            // On-disk size of compressed / sparse files (ТЗ §15: "Estimated" reclaimable space).
            uint high;
            uint low = Native.GetCompressedFileSizeW(ToExtendedPath(path), out high);
            if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0)
                identity.AllocatedSize = ((long)high << 32) | low;
        }
        return FileStatus.Ok;
    }

    public override bool TryGetDirectoryIdentity(string path, out (ulong Vol, ulong Lo, ulong Hi) id)
    {
        id = default;
        using var h = OpenForAttributes(path, out _);
        if (h.IsInvalid) return false;
        if (!TryGetId(h, out var vol, out var lo, out var hi)) return false;
        id = (vol, lo, hi);
        return true;
    }

    public override bool TryGetSnapshot(SafeFileHandle handle, string path, out MetaSnapshot s)
    {
        s = default;
        Native.FILE_BASIC_INFO basic;
        Native.FILE_STANDARD_INFO std;
        if (!Native.GetFileInformationByHandleEx(handle, Native.FileBasicInfo, &basic, (uint)sizeof(Native.FILE_BASIC_INFO)))
            return false;
        if (!Native.GetFileInformationByHandleEx(handle, Native.FileStandardInfo, &std, (uint)sizeof(Native.FILE_STANDARD_INFO)))
            return false;
        s.Size = std.EndOfFile;
        s.LastWriteTicks = basic.LastWriteTime;
        s.ChangeTicks = basic.ChangeTime;
        s.CreationTicks = basic.CreationTime;
        s.LinkCount = std.NumberOfLinks;
        s.AllocatedSize = std.AllocationSize;
        if (TryGetId(handle, out var vol, out var lo, out var hi)) { s.VolumeSerial = vol; s.FileIdLow = lo; s.FileIdHigh = hi; }
        return true;
    }

    public override SafeFileHandle OpenRead(string path, bool sequential) =>
        // .NET adds the \\?\ prefix itself for long paths.
        base.OpenRead(path, sequential);

    // ---------------- Storage profiling (ТЗ §16) ----------------

    private static string GetVolumeRoot(string path)
    {
        var volPath = new char[32768];
        string volumeRoot;
        fixed (char* p = volPath)
        {
            volumeRoot = Native.GetVolumePathNameW(ToExtendedPath(path), p, (uint)volPath.Length)
                ? new string(p) : (Path.GetPathRoot(path) ?? path);
        }
        if (volumeRoot.StartsWith(@"\\?\", StringComparison.Ordinal) && !volumeRoot.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
            volumeRoot = volumeRoot.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + volumeRoot.Substring(8) : volumeRoot.Substring(4);
        return volumeRoot;
    }

    /// <summary>"\\?\Volume{GUID}" (no trailing slash) or "\\.\C:" for a local volume root.</summary>
    private static string VolumeDevice(string volumeRoot)
    {
        var guid = new char[64];
        fixed (char* g = guid)
        {
            string mp = volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + "\\";
            return Native.GetVolumeNameForVolumeMountPointW(mp, g, (uint)guid.Length)
                ? new string(g).TrimEnd('\\')
                : @"\\.\" + volumeRoot.TrimEnd('\\');
        }
    }

    public override StorageProfile GetStorageProfile(string path) =>
        _profiles.GetOrAdd(GetVolumeRoot(path), ProbeVolume);

    private static StorageProfile ProbeVolume(string volumeRoot)
    {
        try
        {
            if (volumeRoot.StartsWith(@"\\", StringComparison.Ordinal) && !volumeRoot.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
                return new StorageProfile("net:" + volumeRoot.ToUpperInvariant(), StorageKind.Network, "Сетевой ресурс");

            uint type = Native.GetDriveTypeW(volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + "\\");
            if (type == Native.DRIVE_REMOTE)
                return new StorageProfile("net:" + volumeRoot.ToUpperInvariant(), StorageKind.Network, "Сетевой диск");

            string device = VolumeDevice(volumeRoot);

            using var h = Native.CreateFileW(device, 0, Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
                return new StorageProfile("vol:" + volumeRoot.ToUpperInvariant(), StorageKind.Unknown, "Неизвестный носитель");

            // Several volumes on one physical disk share one domain.
            string key = "vol:" + volumeRoot.ToUpperInvariant();
            Native.STORAGE_DEVICE_NUMBER num;
            uint ret;
            if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, &num, (uint)sizeof(Native.STORAGE_DEVICE_NUMBER), out ret, IntPtr.Zero))
                key = $"disk:{num.DeviceType}:{num.DeviceNumber}";

            bool? seekPenalty = null;
            var q = new Native.STORAGE_PROPERTY_QUERY { PropertyId = Native.StorageDeviceSeekPenaltyProperty, QueryType = 0 };
            Native.DEVICE_SEEK_PENALTY_DESCRIPTOR sp;
            if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, &q, (uint)sizeof(Native.STORAGE_PROPERTY_QUERY), &sp, (uint)sizeof(Native.DEVICE_SEEK_PENALTY_DESCRIPTOR), out ret, IntPtr.Zero))
                seekPenalty = sp.IncursSeekPenalty != 0;

            uint busType = 0;
            q.PropertyId = Native.StorageDeviceProperty;
            byte* buf = stackalloc byte[1024];
            if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, &q, (uint)sizeof(Native.STORAGE_PROPERTY_QUERY), buf, 1024, out ret, IntPtr.Zero) && ret >= 32)
                busType = *(uint*)(buf + 28);

            if (seekPenalty == true)
                return new StorageProfile(key, StorageKind.Hdd, "HDD");
            if (busType == Native.BusTypeNvme)
                return new StorageProfile(key, StorageKind.Nvme, "NVMe SSD");
            if (seekPenalty == false)
                return new StorageProfile(key, StorageKind.Ssd, "SSD");
            return new StorageProfile(key, StorageKind.Unknown, "Неизвестный носитель");
        }
        catch
        {
            return new StorageProfile("vol:" + volumeRoot.ToUpperInvariant(), StorageKind.Unknown, "Неизвестный носитель");
        }
    }

    // ---------------- Alternate data streams (ТЗ §2, "Compare alternate streams") ----------------

    public override FileStatus ListAlternateStreams(string path, List<(string Name, long Size)> streams)
    {
        Native.WIN32_FIND_STREAM_DATA data;
        IntPtr h = Native.FindFirstStreamW(ToExtendedPath(path), 0, out data, 0);
        if (h == Native.INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 5 or 2 or 3) return FileStatusText.FromWin32(err);
            return FileStatus.Ok;   // ERROR_HANDLE_EOF or a file system without streams
        }
        try
        {
            do
            {
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data.cStreamName);
                if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                    streams.Add((name.ToString(), data.StreamSize));
            }
            while (Native.FindNextStreamW(h, out data));
            int last = Marshal.GetLastWin32Error();
            if (last is not (38 or 18)) return FileStatusText.FromWin32(last);
        }
        finally { Native.FindClose(h); }
        return FileStatus.Ok;
    }

    public override SafeFileHandle OpenAlternateStream(string path, string streamName)
    {
        var h = Native.CreateFileW(ToExtendedPath(path) + streamName, Native.GENERIC_READ, Native.FILE_SHARE_ALL,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw err switch
            {
                5 => new UnauthorizedAccessException(new System.ComponentModel.Win32Exception(err).Message),
                2 or 3 => new FileNotFoundException(new System.ComponentModel.Win32Exception(err).Message, path + streamName),
                _ => new IOException(new System.ComponentModel.Win32Exception(err).Message, unchecked((int)0x80070000) | err),
            };
        }
        return h;
    }

    // ---------------- NTFS USN Journal (ТЗ §19) ----------------

    public bool TryGetVolume(string path, out string volumeRoot, out ulong volumeSerial)
    {
        volumeSerial = 0;
        volumeRoot = GetVolumeRoot(path);
        if (volumeRoot.StartsWith(@"\\", StringComparison.Ordinal) && !volumeRoot.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase))
            return false;   // network share: no journal
        if (!TryGetDirectoryIdentity(volumeRoot, out var id)) return false;
        volumeSerial = id.Vol;
        return true;
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        string device = VolumeDevice(volumeRoot);
        // GENERIC_READ needs administrator rights; FILE_READ_ATTRIBUTES works with the unprivileged FSCTL.
        var h = Native.CreateFileW(device, Native.GENERIC_READ, Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return Native.CreateFileW(device, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
    }

    public bool TryQueryJournal(string volumeRoot, out ulong journalId, out long nextUsn, out long lowestValidUsn)
    {
        journalId = 0; nextUsn = 0; lowestValidUsn = 0;
        try
        {
            using var h = OpenVolume(volumeRoot);
            if (h.IsInvalid) return false;
            Native.USN_JOURNAL_DATA_V0 data;
            if (!Native.DeviceIoControl(h, Native.FSCTL_QUERY_USN_JOURNAL, null, 0, &data, (uint)sizeof(Native.USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
                return false;
            journalId = data.UsnJournalID;
            nextUsn = data.NextUsn;
            lowestValidUsn = data.LowestValidUsn;
            return true;
        }
        catch { return false; }
    }

    public bool TryReadChanges(string volumeRoot, ulong journalId, long fromUsn, long toUsn, HashSet<(ulong Lo, ulong Hi)> changed, CancellationToken ct)
    {
        try
        {
            using var h = OpenVolume(volumeRoot);
            if (h.IsInvalid) return false;
            const int BufSize = 1 << 16;
            byte* buf = (byte*)NativeMemory.Alloc(BufSize);
            try
            {
                long usn = fromUsn;
                bool unprivileged = false;
                while (usn < toUsn)
                {
                    ct.ThrowIfCancellationRequested();
                    var req = new Native.READ_USN_JOURNAL_DATA_V0
                    {
                        StartUsn = usn, ReasonMask = 0xFFFFFFFF, ReturnOnlyOnClose = 0,
                        Timeout = 0, BytesToWaitFor = 0, UsnJournalID = journalId,
                    };
                    uint got;
                    bool ok = Native.DeviceIoControl(h, unprivileged ? Native.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL : Native.FSCTL_READ_USN_JOURNAL,
                        &req, (uint)sizeof(Native.READ_USN_JOURNAL_DATA_V0), buf, BufSize, out got, IntPtr.Zero);
                    if (!ok && !unprivileged)
                    {
                        unprivileged = true;
                        ok = Native.DeviceIoControl(h, Native.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL,
                            &req, (uint)sizeof(Native.READ_USN_JOURNAL_DATA_V0), buf, BufSize, out got, IntPtr.Zero);
                    }
                    if (!ok || got < 8) return false;
                    long next = *(long*)buf;
                    uint off = 8;
                    while (off + 8 <= got)
                    {
                        byte* rec = buf + off;
                        uint len = *(uint*)rec;
                        if (len < 8 || len > got - off) return false;
                        ushort major = *(ushort*)(rec + 4);
                        if (major == 2 && len >= 60)
                            changed.Add((*(ulong*)(rec + 8), 0));
                        else if (major == 3 && len >= 76)
                            changed.Add((*(ulong*)(rec + 8), *(ulong*)(rec + 16)));
                        else if (major == 4 && len >= 64)
                            changed.Add((*(ulong*)(rec + 8), *(ulong*)(rec + 16)));
                        else return false;
                        off += len;
                    }
                    if (off != got) return false;
                    if (next <= usn) return false; // The requested journal interval was not fully covered.
                    usn = next;
                }
                return true;
            }
            finally { NativeMemory.Free(buf); }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_STREAM_DATA
        {
            public long StreamSize;
            public fixed char cStreamName[296];
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr FindFirstStreamW(string lpFileName, int InfoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);
        public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
        public const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903AB;

        [StructLayout(LayoutKind.Sequential)]
        public struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct READ_USN_JOURNAL_DATA_V0
        {
            public long StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
        public const int FindExInfoBasic = 1;
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;
        public const uint FILE_READ_ATTRIBUTES = 0x80;
        public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const int FileBasicInfo = 0;
        public const int FileStandardInfo = 1;
        public const int FileIdInfo = 18;
        public const int FileIdExtdDirectoryInfo = 19;
        public const int FileIdExtdDirectoryRestartInfo = 20;
        public const uint FILE_LIST_DIRECTORY = 0x1;
        public const uint SYNCHRONIZE = 0x100000;
        public const uint DRIVE_REMOTE = 4;
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7;
        public const uint BusTypeNvme = 17;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        public struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public long ftCreationTime;
            public long ftLastAccessTime;
            public long ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            public fixed char cFileName[260];
            public fixed char cAlternateFileName[14];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_ID_INFO
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public long ftCreationTime;
            public long ftLastAccessTime;
            public long ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_STANDARD_INFO
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_BASIC_INFO
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_DEVICE_NUMBER
        {
            public uint DeviceType;
            public uint DeviceNumber;
            public uint PartitionNumber;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
            int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int infoClass, void* lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, BY_HANDLE_FILE_INFORMATION* lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNameW(string lpszFileName, char* lpszVolumePathName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, char* lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint GetDriveTypeW(string lpRootPathName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer, uint nInBufferSize,
            void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
