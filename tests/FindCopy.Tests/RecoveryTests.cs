using FindCopy.Core;

static class RecoveryTests
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("R9 recovery journal survives restart and preserves file version", () =>
        {
            string path = Path.Combine(root, "recovery-journal"); var journal = new RecoveryJournal(path);
            var version = new MetaSnapshot { VolumeSerial = 1, FileIdLow = 2, Size = 3,
                CreationTicks = 4, LastWriteTicks = 5, ChangeTicks = 6 };
            var entry = new RecoveryEntry(Guid.NewGuid(), "original", "staged", null, null, version, DateTime.UtcNow, RecoveryState.Staged);
            journal.Save(entry);
            var loaded = new RecoveryJournal(path).Load(out var errors).Single();
            Require(errors.Count == 0 && loaded == entry && loaded.Version.Equals(version), "journal round trip");
            journal.Save(entry with { State = RecoveryState.Restored });
            Require(new RecoveryJournal(path).Get(entry.Id).State == RecoveryState.Restored &&
                !Directory.EnumerateFiles(path, "*.tmp").Any(), "atomic update or history lost");
        });
        test("R10 malformed recovery history is reported without losing valid entries", () =>
        {
            string path = Path.Combine(root, "recovery-corruption"); var journal = new RecoveryJournal(path);
            var entry = new RecoveryEntry(Guid.NewGuid(), "original", "staged", null, null,
                new MetaSnapshot { VolumeSerial = 1, FileIdLow = 2 }, DateTime.UtcNow, RecoveryState.Staged);
            journal.Save(entry); string bad = Path.Combine(path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(bad, "{broken");
            var loaded = journal.Load(out var errors);
            Require(loaded.Single().Id == entry.Id && errors.Count == 1 && File.Exists(bad), "corrupt history silently discarded");
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
