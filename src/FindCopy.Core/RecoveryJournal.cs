using System.Text.Json;

namespace FindCopy.Core;

public enum RecoveryState { Staged, Recycled, Restored }

public sealed record RecoveryEntry(Guid Id, string OriginalPath, string StagedPath,
    string? RecyclePath, string? MetadataHash, MetaSnapshot Version, DateTime DeletedUtc, RecoveryState State, int FormatVersion = 1);

public sealed record RecoveryOutcome(Guid Id, string Path, bool Restored, string? Reason);

/// <summary>Durable per-user history. Entries precede staging; completed restores stay in history.</summary>
public sealed class RecoveryJournal
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public string DirectoryPath { get; }
    public static string DefaultDirectory => Path.Combine(Path.GetDirectoryName(ScanCache.DefaultPath())!, "recovery");

    public RecoveryJournal(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);

    public IReadOnlyList<RecoveryEntry> Load(out IReadOnlyList<string> errors)
    {
        var entries = new List<RecoveryEntry>(); var problems = new List<string>();
        if (Directory.Exists(DirectoryPath))
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
                try { entries.Add(Read(file)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { problems.Add(Path.GetFileName(file) + ": " + ex.Message); }
        errors = problems;
        return entries.OrderByDescending(e => e.DeletedUtc).ToList();
    }

    internal RecoveryEntry Get(Guid id) => Read(Path.Combine(DirectoryPath, id.ToString("N") + ".json"));
    internal void Remove(Guid id) => File.Delete(Path.Combine(DirectoryPath, id.ToString("N") + ".json"));

    private static RecoveryEntry Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 131072) throw new InvalidDataException("Recovery entry is too large");
        var e = JsonSerializer.Deserialize<RecoveryEntry>(stream, Json) ?? throw new InvalidDataException("Empty recovery entry");
        if (e.FormatVersion != 1 || e.Id == Guid.Empty || Path.GetFileNameWithoutExtension(path) != e.Id.ToString("N") ||
            !Enum.IsDefined(e.State) || !e.Version.HasIdentity || e.Version.Size < 0 ||
            string.IsNullOrWhiteSpace(e.OriginalPath) || string.IsNullOrWhiteSpace(e.StagedPath))
            throw new InvalidDataException("Invalid recovery entry");
        return e;
    }

    internal void Save(RecoveryEntry entry)
    {
        Directory.CreateDirectory(DirectoryPath);
        string target = Path.Combine(DirectoryPath, entry.Id.ToString("N") + ".json");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, entry, Json); stream.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
