using System.IO;
using System.Text.Json;
using FindCopy.Core;

namespace FindCopy.App;

/// <summary>Performance settings the user can tune from benchmark results (ТЗ §26). Saved as JSON.</summary>
public sealed class AppSettings
{
    public int SmallFileThresholdKiB { get; set; } = 1024;
    public int SampleSizeKiB { get; set; } = 64;
    public int StreamBufferKiB { get; set; } = 1024;
    public int ReadersHdd { get; set; } = 1;
    public int ReadersSsd { get; set; } = 2;
    public int ReadersNvme { get; set; } = 4;
    public bool NvmeAutotune { get; set; } = true;
    public int ReadersNetwork { get; set; } = 1;
    public int ReadersUnknown { get; set; } = 1;
    public bool FastEnumeration { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindCopy", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()).Sanitized();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public AppSettings Sanitized()
    {
        SmallFileThresholdKiB = Math.Clamp(SmallFileThresholdKiB, 64, 65536);
        SampleSizeKiB = Math.Clamp(SampleSizeKiB, 4, 1024);
        if (SampleSizeKiB > SmallFileThresholdKiB) SampleSizeKiB = SmallFileThresholdKiB;
        StreamBufferKiB = Math.Clamp(StreamBufferKiB, 64, 16384);
        ReadersHdd = Math.Clamp(ReadersHdd, 1, 16);
        ReadersSsd = Math.Clamp(ReadersSsd, 1, 16);
        ReadersNvme = Math.Clamp(ReadersNvme, 1, 16);
        ReadersNetwork = Math.Clamp(ReadersNetwork, 1, 16);
        ReadersUnknown = Math.Clamp(ReadersUnknown, 1, 16);
        return this;
    }

    public Tuning ToTuning() => new()
    {
        SmallFileThreshold = SmallFileThresholdKiB * 1024L,
        SampleSize = SampleSizeKiB * 1024,
        StreamBufferSize = StreamBufferKiB * 1024,
        FullReadersHdd = ReadersHdd,
        FullReadersSsd = ReadersSsd,
        FullReadersNvme = ReadersNvme,
        FullReadersNvmeStart = Math.Min(2, ReadersNvme),
        AutotuneNvme = NvmeAutotune,
        FullReadersNetwork = ReadersNetwork,
        FullReadersUnknown = ReadersUnknown,
    };

    public EnumerationBackend Backend => FastEnumeration ? EnumerationBackend.Auto : EnumerationBackend.Win32Only;
}
