namespace FindCopy.Core;

public enum FileStatus : byte
{
    Ok = 0,
    AccessDenied,
    FileNotFoundDuringScan,
    SharingViolation,
    IoError,
    ChangedDuringScan,
    CloudContentNotLocal,
    ReparseSkipped,
    Unsupported,
    Cancelled,
}

public enum VerificationState
{
    HashMatch,
    ExactMatch,
}

public enum StorageKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Network,
}

public static class FileStatusText
{
    public static string ToCode(FileStatus s) => s switch
    {
        FileStatus.Ok => "OK",
        FileStatus.AccessDenied => "ACCESS_DENIED",
        FileStatus.FileNotFoundDuringScan => "FILE_NOT_FOUND_DURING_SCAN",
        FileStatus.SharingViolation => "SHARING_VIOLATION",
        FileStatus.IoError => "IO_ERROR",
        FileStatus.ChangedDuringScan => "CHANGED_DURING_SCAN",
        FileStatus.CloudContentNotLocal => "CLOUD_CONTENT_NOT_LOCAL",
        FileStatus.ReparseSkipped => "REPARSE_SKIPPED",
        FileStatus.Unsupported => "UNSUPPORTED",
        FileStatus.Cancelled => "CANCELLED",
        _ => s.ToString(),
    };

    public static FileStatus FromException(Exception ex) => ex switch
    {
        OperationCanceledException => FileStatus.Cancelled,
        UnauthorizedAccessException => FileStatus.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => FileStatus.FileNotFoundDuringScan,
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => FileStatus.SharingViolation,
        IOException io when (io.HResult & 0xFFFF) is 2 or 3 => FileStatus.FileNotFoundDuringScan,
        IOException io when (io.HResult & 0xFFFF) is 5 => FileStatus.AccessDenied,
        IOException io when (io.HResult & 0xFFFF) is 50 => FileStatus.Unsupported,
        _ => FileStatus.IoError,
    };

    public static FileStatus FromWin32(int error) => error switch
    {
        2 or 3 or 161 => FileStatus.FileNotFoundDuringScan,
        5 => FileStatus.AccessDenied,
        32 or 33 => FileStatus.SharingViolation,
        50 => FileStatus.Unsupported,
        362 or 389 or 390 or 391 or 392 or 395 or 396 or 397 or 398 => FileStatus.CloudContentNotLocal,
        1223 => FileStatus.Cancelled,
        _ => FileStatus.IoError,
    };
}
