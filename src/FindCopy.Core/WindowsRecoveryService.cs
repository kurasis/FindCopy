using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Core;

/// <summary>Restores only recorded objects, by handle, without replacing an existing destination.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsRecoveryService
{
    public RecoveryJournal Journal { get; }
    public WindowsRecoveryService(string? directory = null) => Journal = new RecoveryJournal(directory);

    internal static bool SameVersion(in MetaSnapshot expected, in MetaSnapshot actual) =>
        expected.HasIdentity && actual.HasIdentity && expected.VolumeSerial == actual.VolumeSerial &&
        expected.FileIdLow == actual.FileIdLow && expected.FileIdHigh == actual.FileIdHigh &&
        expected.Size == actual.Size && expected.LastWriteTicks == actual.LastWriteTicks &&
        expected.CreationTicks == actual.CreationTicks;

    internal static void CheckLocalPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        if (full.Length < 3 || full[1] != ':' || full[2] != '\\' || !Path.IsPathFullyQualified(path) ||
            full.IndexOf(':', 2) >= 0 || full.Contains('\0')) throw new IOException("Требуется обычный путь на локальном диске");
    }

    internal static void CheckParents(string path)
    {
        CheckLocalPath(path);
        for (string? parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Восстановление через ссылки на каталоги запрещено: " + parent);
    }

    internal static string BinMetadata(string recyclePath, string stagedPath, long size, out string hash)
    {
        CheckParents(recyclePath);
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string bin = Path.Combine(Path.GetPathRoot(recyclePath)!, "$Recycle.Bin", sid);
        string name = Path.GetFileName(recyclePath);
        if (!string.Equals(Path.GetDirectoryName(recyclePath), bin, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("$R", StringComparison.Ordinal) || name.Length <= 2)
            throw new IOException("Не подтверждён файл в личной корзине");
        string metadata = Path.Combine(bin, "$I" + name[2..]);
        using var handle = new WindowsDeletionBackend().OpenCandidate(metadata);
        byte[] bytes = ReadMetadata(handle);
        ValidateMetadata(bytes, stagedPath, size);
        hash = Convert.ToHexString(SHA256.HashData(bytes));
        return metadata;
    }

    private static byte[] ReadMetadata(SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        if (length is < 28 or > 131072) throw new InvalidDataException("Недопустимая запись корзины");
        byte[] bytes = new byte[(int)length]; int read = 0;
        while (read < bytes.Length)
        {
            int n = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (n == 0) throw new EndOfStreamException("Запись корзины неполна");
            read += n;
        }
        return bytes;
    }

    private static void ValidateMetadata(byte[] bytes, string stagedPath, long size)
    {
        long version = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        int offset = version == 1 ? 24 : version == 2 ? 28 : throw new InvalidDataException("Неизвестный формат корзины");
        int chars = version == 1 ? 260 : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        if (chars <= 0 || chars > 32768 || bytes.Length != offset + chars * 2 ||
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)) != size ||
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) <= 0)
            throw new InvalidDataException("Запись корзины не соответствует файлу");
        string path = Encoding.Unicode.GetString(bytes, offset, chars * 2).TrimEnd('\0');
        if (!string.Equals(path, stagedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Исходный путь в корзине изменён");
    }

    public RecoveryOutcome Restore(Guid id)
    {
        RecoveryEntry? entry = null; bool moved = false;
        try
        {
            entry = Journal.Get(id);
            if (entry.State == RecoveryState.Restored) throw new IOException("Файл уже восстановлен");
            CheckParents(entry.OriginalPath); CheckLocalPath(entry.StagedPath);
            if (!Path.GetFileName(Path.GetDirectoryName(entry.StagedPath)!).Equals(".FindCopy-recycle-" + id.ToString("N"), StringComparison.Ordinal))
                throw new InvalidDataException("Не подтверждён каталог подготовки");
            if (entry.State == RecoveryState.Staged && !File.Exists(entry.StagedPath)) entry = ResolveInterruptedRecycle(entry);
            if (entry.State == RecoveryState.Staged) CheckParents(entry.StagedPath);
            string originalParent = Path.GetDirectoryName(entry.OriginalPath)!;
            if (!Directory.Exists(originalParent)) throw new DirectoryNotFoundException("Исходная папка отсутствует; восстановите её сначала");
            string source = entry.State == RecoveryState.Recycled ? entry.RecyclePath ?? throw new IOException("Путь корзины не сохранён") : entry.StagedPath;
            var backend = new WindowsDeletionBackend();
            SafeFileHandle? metadata = null;
            try
            {
                if (entry.State == RecoveryState.Recycled)
                {
                    // Hold the matching metadata object against replacement through the data move.
                    string path = BinMetadata(source, entry.StagedPath, entry.Version.Size, out _);
                    metadata = OpenLockedFile(path);
                    byte[] bytes = ReadMetadata(metadata); ValidateMetadata(bytes, entry.StagedPath, entry.Version.Size);
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != entry.MetadataHash)
                        throw new IOException("Запись корзины изменена после удаления");
                }
                using var candidate = OpenLockedFile(source);
                var fs = new WindowsFileSystem();
                if (!fs.TryGetSnapshot(candidate, source, out var current) || !SameVersion(entry.Version, current))
                    throw new IOException("Файл изменён или заменён после удаления; автоматическое восстановление отменено");
                var parents = OpenParents(originalParent);
                try
                {
                    var parent = parents[^1];
                    if (!fs.TryGetSnapshot(parent, originalParent, out var parentSnapshot) || parentSnapshot.VolumeSerial != current.VolumeSerial)
                        throw new IOException("Исходная папка находится на другом томе или недоступна");
                    RenameInto(candidate, parent, Path.GetFileName(entry.OriginalPath));
                }
                finally { foreach (var parent in parents) parent.Dispose(); }
                moved = true;
                Journal.Save(entry with { State = RecoveryState.Restored });
                string? note = null;
                if (metadata != null && !backend.DeletePermanently(metadata, source, out var error))
                    note = "Файл восстановлен; запись корзины не очищена: " + error;
                if (entry.State == RecoveryState.Staged)
                {
                    try { File.Delete(Path.Combine(Path.GetDirectoryName(source)!, "original-path.txt")); Directory.Delete(Path.GetDirectoryName(source)!); }
                    catch (IOException) { note = "Файл восстановлен; пустой каталог подготовки не удалён"; }
                }
                return new(id, entry.OriginalPath, true, note);
            }
            finally { metadata?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            System.Text.Json.JsonException or OverflowException or Win32Exception)
        {
            return new(id, entry?.OriginalPath ?? "", moved, moved ? "Файл восстановлен, но история или запись корзины не обновлены: " + ex.Message : ex.Message);
        }
    }

    private RecoveryEntry ResolveInterruptedRecycle(RecoveryEntry entry)
    {
        // The flushed pre-staging entry survives a crash before the shell result is journaled.
        // Locate only its unique private staging path in this user's bin, then verify the object.
        string bin = Path.Combine(Path.GetPathRoot(entry.StagedPath)!, "$Recycle.Bin", WindowsIdentity.GetCurrent().User!.Value);
        RecoveryEntry? found = null;
        if (Directory.Exists(bin))
            foreach (string metadata in Directory.EnumerateFiles(bin, "$I*"))
            {
                string source = Path.Combine(bin, "$R" + Path.GetFileName(metadata)[2..]);
                string? hash = null;
                try
                {
                    BinMetadata(source, entry.StagedPath, entry.Version.Size, out string validated);
                    using var file = OpenLockedFile(source);
                    if (new WindowsFileSystem().TryGetSnapshot(file, source, out var actual) && SameVersion(entry.Version, actual)) hash = validated;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or Win32Exception)
                { /* Other entries, incomplete bin operations, and uncertain objects are never adopted. */ }
                if (hash == null) continue;
                if (found != null) throw new IOException("В корзине несколько подходящих записей; автоматическое восстановление отменено");
                found = entry with { State = RecoveryState.Recycled, RecyclePath = source, MetadataHash = hash };
            }
        if (found == null) throw new FileNotFoundException("Записанный файл не найден ни в каталоге подготовки, ни в личной корзине");
        Journal.Save(found);
        return found;
    }

    private static List<SafeFileHandle> OpenParents(string path)
    {
        var paths = new Stack<string>();
        for (string? p = path; p != null; p = Path.GetDirectoryName(p)) paths.Push(p);
        var handles = new List<SafeFileHandle>();
        try
        {
            // Hold each ancestor against renaming/replacement, opening reparse objects themselves.
            while (paths.TryPop(out var p)) handles.Add(OpenParent(p, paths.Count == 0));
            return handles;
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }

    private static SafeFileHandle OpenLockedFile(string path)
    {
        var h = CreateFileW(WindowsFileSystem.ToExtendedPath(path), 0x80010000 /* READ | DELETE */, 1,
            IntPtr.Zero, 3, 0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (h.IsInvalid) { int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        uint* attributes = stackalloc uint[2];
        if (!GetFileInformationByHandleEx(h, 9, attributes, 8) ||
            (attributes[0] & FileAttr.Directory) != 0 ||
            ((attributes[0] & FileAttr.ReparsePoint) != 0 && ReparseTags.IsNameSurrogateLink(attributes[1])))
        { h.Dispose(); throw new IOException("Не подтверждён обычный файл восстановления"); }
        return h;
    }

    private static SafeFileHandle OpenParent(string path, bool destination)
    {
        // No DELETE sharing keeps the original destination directory from moving during restore.
        var h = CreateFileW(WindowsFileSystem.ToExtendedPath(path), destination ? 0xA2u : 0xA0u /* READ_ATTRIBUTES | TRAVERSE | optional ADD_FILE */, 3,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (h.IsInvalid) { int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        uint* attributes = stackalloc uint[2];
        if (!GetFileInformationByHandleEx(h, 9, attributes, 8) || (attributes[0] & FileAttr.ReparsePoint) != 0)
        { h.Dispose(); throw new IOException("Исходный каталог является ссылкой или недоступен"); }
        return h;
    }

    private static void RenameInto(SafeFileHandle file, SafeFileHandle parent, string name)
    {
        // ReplaceIfExists remains FALSE. A destination created concurrently is preserved too.
        int offset = IntPtr.Size == 8 ? 20 : 12;
        byte[] info = new byte[offset + name.Length * 2];
        fixed (byte* p = info)
        {
            *(IntPtr*)(p + (IntPtr.Size == 8 ? 8 : 4)) = parent.DangerousGetHandle();
            *(uint*)(p + offset - 4) = (uint)(name.Length * 2);
            name.AsSpan().CopyTo(new Span<char>(p + offset, name.Length));
            // The Win32 wrapper does not accept a non-null RootDirectory on all supported hosts.
            // Native FileRenameInformation accepts the held directory handle without path resolution.
            nint* statusBlock = stackalloc nint[2];
            int status = NtSetInformationFile(file, statusBlock, p, (uint)info.Length, 10 /* FileRenameInformation */);
            if (status < 0)
            {
                int error = (int)RtlNtStatusToDosError(status);
                throw new Win32Exception(error, "Не удалось восстановить файл; существующие файлы не перезаписываются: " +
                    new Win32Exception(error).Message + " (" + error + ")");
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int cls, void* info, uint size);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle file, void* status, void* info, uint size, int cls);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}
