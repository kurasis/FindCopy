using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Core;

public enum DeleteMode
{
    /// <summary>Move to the Recycle Bin (default). Refused where the volume has no Recycle Bin.</summary>
    RecycleBin,
    /// <summary>Delete permanently.</summary>
    Permanent,
}

/// <summary>Copies the user chose to delete from one duplicate group. At least one copy must stay.</summary>
public sealed record DeleteRequest(DuplicateGroup Group, IReadOnlyList<DuplicateFile> ToDelete);

public sealed record DeleteOutcome(string Path, bool Deleted, string? Reason, long FreedBytes);

/// <summary>Platform primitives for deletion. Every method is used only after the safety checks.</summary>
public interface IDeletionBackend
{
    /// <summary>Opens the copy that is kept: read access, others may read but not write or delete it.</summary>
    SafeFileHandle OpenKeeper(string path);
    /// <summary>
    /// Opens a copy to be deleted: read + delete access, no other writers. Fails with a sharing
    /// violation when it is the very same file object as an open keeper (another path to it).
    /// </summary>
    SafeFileHandle OpenCandidate(string path);
    /// <summary>Permanently deletes the file behind an open candidate handle.</summary>
    bool DeletePermanently(SafeFileHandle candidate, string path, out string? error);
    /// <summary>True when files on this path's volume can go to the Recycle Bin.</summary>
    bool RecycleBinAvailable(string path);
    bool MoveToRecycleBin(string path, out string? error);
    /// <summary>Blocks writers without requiring the shell to share DELETE access with this guard.</summary>
    SafeFileHandle OpenRecycleGuard(string stagedPath) => File.OpenHandle(stagedPath, FileMode.Open,
        FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.SequentialScan);
    /// <summary>Atomically moves the verified open object into a private same-volume namespace.</summary>
    bool StageForRecycle(SafeFileHandle candidate, string originalPath, out string stagedPath, out string? error)
    {
        stagedPath = "";
        error = "Безопасное перемещение в корзину не поддерживается";
        return false;
    }
}

/// <summary>
/// Deletes duplicates the user selected, re-checking everything first (ТЗ §11, §25): a fresh
/// byte-for-byte comparison with a copy that stays (never the cache, never just a hash), unchanged
/// size/date since the scan, and proof that the kept copy is a different file object (hard links,
/// the same network share under two names). Any doubt means the file is not deleted.
/// </summary>
public sealed class DuplicateDeleter
{
    private readonly IFileSystem _fs;
    private readonly IDeletionBackend _backend;
    private const int BufferSize = 1 << 20;

    public DuplicateDeleter(IFileSystem? fs = null, IDeletionBackend? backend = null)
    {
        _fs = fs ?? FileSystemBase.CreateDefault();
        _backend = backend ?? CreateDefaultBackend();
    }

    public static IDeletionBackend CreateDefaultBackend() =>
        OperatingSystem.IsWindows() ? new WindowsDeletionBackend() : new PortableDeletionBackend();

    public IDeletionBackend Backend => _backend;

    /// <summary>Paths (incl. hard-link aliases) whose volume has no Recycle Bin.</summary>
    public IReadOnlyList<string> PathsWithoutRecycleBin(IEnumerable<DeleteRequest> requests) =>
        requests.SelectMany(r => r.ToDelete).SelectMany(f => new[] { f.Path }.Concat(f.HardLinkAliases)).Where(p => !_backend.RecycleBinAvailable(p)).ToList();

    public List<DeleteOutcome> Run(IReadOnlyList<DeleteRequest> requests, DeleteMode mode,
        IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken ct = default) =>
        Run(requests, _ => mode, progress, ct);

    /// <param name="modeFor">Deletion mode per file path (e.g. permanent only where there is no Recycle Bin).</param>
    public List<DeleteOutcome> Run(IReadOnlyList<DeleteRequest> requests, Func<string, DeleteMode> modeFor,
        IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken ct = default)
    {
        var outcomes = new List<DeleteOutcome>();
        int total = requests.Sum(r => r.ToDelete.Count), done = 0;
        byte[] a = ArrayPool<byte>.Shared.Rent(BufferSize), b = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var req in requests)
            {
                var g = req.Group;
                var selected = new HashSet<DuplicateFile>(req.ToDelete);
                void SkipAll(string reason)
                {
                    foreach (var f in req.ToDelete) { outcomes.Add(new DeleteOutcome(f.Path, false, reason, 0)); progress?.Report((++done, total, f.Path)); }
                }

                if (selected.Count == 0) continue;
                if (!selected.All(g.Files.Contains)) { SkipAll("Файл не из этой группы"); continue; }
                var keepers = g.Files.Where(f => !selected.Contains(f)).ToList();
                if (keepers.Count == 0) { SkipAll("В группе должна остаться хотя бы одна копия"); continue; }

                // The kept copy stays open (no write/delete sharing) for the whole group.
                SafeFileHandle? keeperHandle = null;
                DuplicateFile? keeper = null;
                FileIdentity keeperId = default;
                foreach (var k in keepers)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var h = _backend.OpenKeeper(k.Path);
                        if (!_fs.TryGetSnapshot(h, k.Path, out var snapshot) || !snapshot.HasIdentity ||
                            !MatchesScan(k, snapshot) || snapshot.Size != g.LogicalSize) { h.Dispose(); continue; }
                        keeperHandle = h;
                        keeper = k;
                        keeperId = IdentityOf(snapshot);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
                if (keeperHandle == null || keeper == null)
                {
                    SkipAll("Не осталось доступной неизменённой копии, с которой можно сверить файл");
                    continue;
                }

                using (keeperHandle)
                {
                    foreach (var f in req.ToDelete)
                    {
                        ct.ThrowIfCancellationRequested();
                        var outcome = DeleteOne(g, f, keeper, keeperHandle, keeperId, modeFor(f.Path), a, b, ct);
                        outcomes.Add(outcome);
                        progress?.Report((++done, total, f.Path));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
        return outcomes;
    }

    private DeleteOutcome DeleteOne(DuplicateGroup g, DuplicateFile f, DuplicateFile keeper, SafeFileHandle keeperHandle,
        FileIdentity keeperId, DeleteMode mode, byte[] a, byte[] b, CancellationToken ct)
    {
        DeleteOutcome No(string reason) => new(f.Path, false, reason, 0);

        if (string.Equals(f.Path, keeper.Path, StringComparison.OrdinalIgnoreCase))
            return No("Это та же копия, что остаётся");
        if (mode == DeleteMode.RecycleBin && !_backend.RecycleBinAvailable(f.Path))
            return No("На этом диске нет корзины (флешка или сетевая папка). Выберите безвозвратное удаление");

        // Replaced by a link since the scan? Never delete through a link.
        try
        {
            if (new FileInfo(f.Path).LinkTarget != null || new FileInfo(keeper.Path).LinkTarget != null)
                return No("Файл стал ссылкой после поиска");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return No("Файл недоступен: " + ex.Message); }

        // Same physical object under another path (hard link, network share under two names)?
        var idStatus = _fs.GetIdentity(f.Path, out var candId);
        if (idStatus != FileStatus.Ok) return No("Файл недоступен: " + FileStatusText.ToCode(idStatus));
        if (candId.Valid && keeperId.Valid && candId.VolumeSerial == keeperId.VolumeSerial &&
            candId.FileIdLow == keeperId.FileIdLow && candId.FileIdHigh == keeperId.FileIdHigh)
            return No("Это тот же файл, что и оставляемая копия (жёсткая ссылка или другой путь к нему)");

        SafeFileHandle cand;
        try { cand = _backend.OpenCandidate(f.Path); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return No("Файл занят другой программой или это тот же файл, что и оставляемая копия");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return No("Не удалось открыть: " + ex.Message); }

        bool closed = false;
        SafeFileHandle? recycleGuard = null;
        string? recoveryPath = null;
        try
        {
            // Unchanged since the scan: the user decided about exactly this file.
            if (!_fs.TryGetSnapshot(cand, f.Path, out var before) || !before.HasIdentity)
                return No("Метаданные открытого файла недоступны; удаление запрещено");
            if (!MatchesScan(f, before) || (candId.Valid && !SameObject(before, IdentityOf(candId))))
                return No("Открытый файл заменён после поиска или проверки пути");
            if (!_fs.TryGetSnapshot(keeperHandle, keeper.Path, out var keeperBefore) || !MatchesScan(keeper, keeperBefore) ||
                !SameObject(keeperBefore, IdentityOf(keeperId))) return No("Оставляемая копия изменилась");
            if (SameObject(before, keeperBefore)) return No("Удаляемая и оставляемая копии — один файл");
            long len = before.Size;
            if (len != g.LogicalSize) return No("Файл изменился после поиска (другой размер)");
            if (before.LastWriteTicks != 0 && f.LastWriteUtc.Ticks > 0 &&
                before.LastWriteTicks != f.LastWriteUtc.ToFileTimeUtc() && before.LastWriteTicks != f.LastWriteUtc.Ticks)
                return No("Файл изменился после поиска (другая дата изменения)");

            // Fresh byte-for-byte comparison with the kept copy.
            long off = 0;
            while (off < g.LogicalSize)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(BufferSize, g.LogicalSize - off);
                int na = ReadFull(keeperHandle, a.AsSpan(0, want), off);
                int nb = ReadFull(cand, b.AsSpan(0, want), off);
                if (na != want || nb != want) return No("Файл изменился во время проверки");
                if (!a.AsSpan(0, want).SequenceEqual(b.AsSpan(0, want)))
                    return No("Содержимое не совпадает с оставляемой копией. Файл не удалён");
                off += want;
            }
            if (!_fs.TryGetSnapshot(cand, f.Path, out var after) || !after.Equals(before) ||
                !_fs.TryGetSnapshot(keeperHandle, keeper.Path, out var keeperAfter) || !keeperAfter.Equals(keeperBefore))
                return No("Файл изменился во время проверки");

            // Retain the verified handle during permanent alias removal; writers remain excluded on Windows.
            string? err;
            string? recycleNote = null;
            if (mode == DeleteMode.Permanent)
            {
                if (!_backend.DeletePermanently(cand, f.Path, out err)) return No("Не удалось удалить: " + err);
            }
            else
            {
                if (!_backend.StageForRecycle(cand, f.Path, out var staged, out err))
                    return No("Не удалось безопасно подготовить корзину: " + err);
                recoveryPath = staged;
                recycleGuard = _backend.OpenRecycleGuard(staged);
                if (!_fs.TryGetSnapshot(recycleGuard, staged, out var guarded) || !SameObject(before, guarded) ||
                    guarded.Size != before.Size || guarded.LastWriteTicks != before.LastWriteTicks)
                    return new DeleteOutcome(f.Path, true, "Не удалось защитить проверенный файл; он сохранён в " + staged, 0);
                cand.Dispose();
                closed = true;
                if (!_backend.MoveToRecycleBin(staged, out err))
                    return new DeleteOutcome(f.Path, true, "Проверенный файл сохранён в " + staged + ": " + err, 0);
                recycleNote = err;
            }

            var aliasProblems = new List<string>();
            foreach (var alias in f.HardLinkAliases)
            {
                ct.ThrowIfCancellationRequested();
                if (_fs.GetIdentity(alias, out var aid) != FileStatus.Ok || !aid.Valid ||
                    !SameObject(before, IdentityOf(aid)))
                {
                    aliasProblems.Add(alias + " (ссылка заменена или недоступна, оставлена)");
                    continue;
                }
                try
                {
                    using var ah = _backend.OpenCandidate(alias);
                    if (!_fs.TryGetSnapshot(ah, alias, out var aliasSnapshot) ||
                        !SameObject(before, aliasSnapshot) || aliasSnapshot.Size != before.Size ||
                        aliasSnapshot.LastWriteTicks != before.LastWriteTicks)
                    {
                        aliasProblems.Add(alias + " (открытый объект заменён, оставлен)");
                        continue;
                    }
                    bool ok;
                    if (mode == DeleteMode.Permanent) ok = _backend.DeletePermanently(ah, alias, out err);
                    else if (_backend.RecycleBinAvailable(alias) && _backend.StageForRecycle(ah, alias, out var staged, out err))
                    {
                        ah.Dispose();
                        ok = _backend.MoveToRecycleBin(staged, out err);
                        if (!ok) err = "Проверенный файл сохранён в " + staged + ": " + err;
                    }
                    else { ok = false; err = "Безопасное перемещение ссылки в корзину недоступно"; }
                    if (!ok || err != null) aliasProblems.Add(alias + ": " + err);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { aliasProblems.Add(alias + ": " + ex.Message); }
            }

            // Inspect the still-open object, including links created concurrently outside the scanned tree.
            bool lastLinkGone = mode == DeleteMode.Permanent && aliasProblems.Count == 0 &&
                _fs.TryGetSnapshot(cand, f.Path, out var remainingObject) && remainingObject.LinkCount == 0;
            long freed = mode == DeleteMode.Permanent && lastLinkGone && before.AllocatedSize >= 0 ? before.AllocatedSize : 0;
            if (recycleNote != null) aliasProblems.Add(recycleNote);
            string? note = aliasProblems.Count > 0
                ? "Удалён с замечаниями: " + string.Join("; ", aliasProblems)
                : mode == DeleteMode.Permanent && !lastLinkGone ? "Место не освобождено: остались внешние ссылки или их число неизвестно" : null;
            return new DeleteOutcome(f.Path, true, note, freed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return recoveryPath == null ? No("Ошибка: " + ex.Message)
            : new DeleteOutcome(f.Path, true, "Проверенный файл сохранён в " + recoveryPath + ": " + ex.Message, 0); }
        finally
        {
            if (!closed) cand.Dispose();
            recycleGuard?.Dispose();
        }
    }

    private static bool MatchesScan(DuplicateFile file, in MetaSnapshot snapshot) =>
        (file.ScannedVersion is not { } version || version.Equals(snapshot)) &&
        snapshot.Size == file.LogicalSize && snapshot.LastWriteTicks == file.LastWriteUtc.ToFileTimeUtc() &&
        (file.PhysicalIdentity is not { } id ||
            (snapshot.HasIdentity && id.Vol == snapshot.VolumeSerial && id.Lo == snapshot.FileIdLow && id.Hi == snapshot.FileIdHigh));

    private static bool SameObject(in MetaSnapshot a, in MetaSnapshot b) =>
        a.HasIdentity && b.HasIdentity && a.VolumeSerial == b.VolumeSerial &&
        a.FileIdLow == b.FileIdLow && a.FileIdHigh == b.FileIdHigh;

    private static MetaSnapshot IdentityOf(in FileIdentity id) => new()
    {
        VolumeSerial = id.VolumeSerial, FileIdLow = id.Valid ? id.FileIdLow : 0,
        FileIdHigh = id.Valid ? id.FileIdHigh : 0,
    };

    private static FileIdentity IdentityOf(in MetaSnapshot snapshot) => new()
    {
        Valid = snapshot.HasIdentity, VolumeSerial = snapshot.VolumeSerial,
        FileIdLow = snapshot.FileIdLow, FileIdHigh = snapshot.FileIdHigh,
    };

    private static int ReadFull(SafeFileHandle h, Span<byte> buf, long off)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = RandomAccess.Read(h, buf[got..], off + got);
            if (n == 0) break;
            got += n;
        }
        return got;
    }
}

/// <summary>Non-Windows backend used by the test-suite: no Recycle Bin, no mandatory share modes.</summary>
public sealed class PortableDeletionBackend : IDeletionBackend
{
    public SafeFileHandle OpenKeeper(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

    public SafeFileHandle OpenCandidate(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.SequentialScan);

    public bool DeletePermanently(SafeFileHandle candidate, string path, out string? error)
    {
        try { File.Delete(path); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool RecycleBinAvailable(string path) => false;

    public bool MoveToRecycleBin(string path, out string? error)
    {
        error = "Корзина недоступна";
        return false;
    }
}

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsDeletionBackend : IDeletionBackend
{
    private readonly RecoveryJournal _recovery;
    public WindowsDeletionBackend(string? recoveryDirectory = null) => _recovery = new RecoveryJournal(recoveryDirectory);
    private const uint GENERIC_READ = 0x80000000, DELETE = 0x00010000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3, FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    public SafeFileHandle OpenKeeper(string path) => Open(path, GENERIC_READ, FILE_SHARE_READ);

    public SafeFileHandle OpenCandidate(string path) => Open(path, GENERIC_READ | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE);

    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        // Inspect the opened reparse object itself so a symlink substituted during open cannot be followed.
        var h = CreateFileW(WindowsFileSystem.ToExtendedPath(path), access, share, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN | 0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (!h.IsInvalid)
        {
            uint* tagInfo = stackalloc uint[2];
            if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, tagInfo, 8) ||
                ((tagInfo[0] & FileAttr.ReparsePoint) != 0 && ReparseTags.IsNameSurrogateLink(tagInfo[1])))
            {
                h.Dispose();
                throw new IOException("Невозможно подтвердить, что файл не является ссылкой");
            }
            return h;
        }
        int err = Marshal.GetLastWin32Error();
        h.Dispose();
        string msg = new System.ComponentModel.Win32Exception(err).Message;
        throw err switch
        {
            5 => new UnauthorizedAccessException(msg),
            2 or 3 => new FileNotFoundException(msg, path),
            _ => new IOException(msg, unchecked((int)0x80070000) | err),
        };
    }

    public bool DeletePermanently(SafeFileHandle candidate, string path, out string? error)
    {
        error = null;
        // FileDispositionInfoEx: DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE (Windows 10 1809+).
        uint flags = 0x1 | 0x2 | 0x10;
        if (SetFileInformationByHandle(candidate, 21 /* FileDispositionInfoEx */, &flags, 4)) return true;
        int err = Marshal.GetLastWin32Error();
        if (err is 87 or 1 or 50 or 124)
        {
            byte del = 1;
            if (SetFileInformationByHandle(candidate, 4 /* FileDispositionInfo */, &del, 1)) return true;
            err = Marshal.GetLastWin32Error();
        }
        error = new System.ComponentModel.Win32Exception(err).Message;
        return false;
    }

    public bool RecycleBinAvailable(string path)
    {
        // Extended DOS paths are local paths; extended UNC paths remain network shares.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':' && path[6] == '\\')
            path = path.Substring(4);
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;  // network share
        uint type = GetDriveTypeW(root.EndsWith('\\') ? root : root + "\\");
        return type == 3; // DRIVE_FIXED; removable (USB), remote, CD and RAM disks have no Recycle Bin
    }

    public bool StageForRecycle(SafeFileHandle candidate, string originalPath, out string stagedPath, out string? error)
    {
        stagedPath = "";
        error = null;
        var fs = new WindowsFileSystem();
        if (!fs.TryGetSnapshot(candidate, originalPath, out var verified) || !verified.HasIdentity)
        { error = "Идентичность открытого файла недоступна"; return false; }
        string filename = Path.GetFileName(originalPath);
        if (filename.Length > 100) filename = "file-" + Guid.NewGuid().ToString("N");
        fs.TryGetVolume(originalPath, out string volumeRoot, out _);
        var parents = new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), volumeRoot,
            Path.GetPathRoot(originalPath)!, Path.GetDirectoryName(originalPath)! }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string parent in parents)
        {
            if (string.IsNullOrEmpty(parent) || !fs.TryGetDirectoryIdentity(parent, out var parentId) || parentId.Vol != verified.VolumeSerial) continue;
            Guid recoveryId = Guid.NewGuid();
            string directory = Path.Combine(parent, ".FindCopy-recycle-" + recoveryId.ToString("N"));
            string destination = Path.Combine(directory, filename);
            // Shell namespaces still impose path limits on some hosts. Handle-bound staging makes
            // a long source path safe without depending on those namespace parsing limits.
            if (destination.Length >= 240) continue;
            try
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).Create(security);
                File.WriteAllText(Path.Combine(directory, "original-path.txt"), originalPath);
                _recovery.Save(new RecoveryEntry(recoveryId, Path.GetFullPath(originalPath), destination, null, null,
                    verified, DateTime.UtcNow, RecoveryState.Staged));
                string extended = WindowsFileSystem.ToExtendedPath(destination);
                int offset = IntPtr.Size == 8 ? 20 : 12;
                byte[] info = new byte[offset + extended.Length * 2];
                fixed (byte* buffer = info)
                {
                    *(uint*)(buffer + offset - 4) = (uint)(extended.Length * 2);
                    extended.AsSpan().CopyTo(new Span<char>(buffer + offset, extended.Length));
                    if (!SetFileInformationByHandle(candidate, 3 /* FileRenameInfo */, buffer, (uint)info.Length))
                    {
                        error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                        _recovery.Remove(recoveryId);
                        File.Delete(Path.Combine(directory, "original-path.txt"));
                        Directory.Delete(directory);
                        continue;
                    }
                }
                stagedPath = destination;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { _recovery.Remove(recoveryId); File.Delete(Path.Combine(directory, "original-path.txt")); Directory.Delete(directory); }
                catch { /* The candidate has not moved; leave any unavailable empty staging directory. */ }
            }
        }
        error ??= "Нет доступного короткого каталога на том же томе для безопасной корзины";
        return false;
    }

    public bool MoveToRecycleBin(string path, out string? error)
    {
        string? parent = Path.GetDirectoryName(path);
        string prefix = ".FindCopy-recycle-";
        if (parent == null || !Path.GetFileName(parent).StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(parent)[prefix.Length..], "N", out var id))
        { error = "Не подтверждён каталог безопасной корзины"; return false; }
        RecoveryEntry entry;
        try
        {
            entry = _recovery.Get(id);
            if (!string.Equals(entry.StagedPath, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь подготовки изменён");
        }
        catch (Exception ex) { error = "История восстановления недоступна: " + ex.Message; return false; }
        if (!WindowsRecycleBin.Move(path, out var recycled, out error)) return false;
        try
        {
            if (recycled == null) throw new IOException("Корзина не вернула путь файла");
            WindowsRecoveryService.BinMetadata(recycled, path, entry.Version.Size, out string hash);
            _recovery.Save(entry with { RecyclePath = recycled, MetadataHash = hash, State = RecoveryState.Recycled });
        }
        catch (Exception ex)
        {
            error = "Файл находится в корзине, но автоматическое восстановление недоступно: " + ex.Message;
            return true;
        }
        try
        {
            if (parent != null && Path.GetFileName(parent).StartsWith(".FindCopy-recycle-", StringComparison.Ordinal))
            {
                File.Delete(Path.Combine(parent, "original-path.txt"));
                Directory.Delete(parent);
            }
        }
        catch { /* A recovery manifest may remain if staging cleanup is unavailable. */ }
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int infoClass, void* info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);
}
