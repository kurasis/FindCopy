namespace FindCopy.Core;

/// <summary>
/// Centralised tuning constants (ТЗ §26). Changing them never changes the algorithm or
/// the meaning of a hash, only performance characteristics.
/// </summary>
public sealed class Tuning
{
    /// <summary>Files up to this size skip sampling and go straight to full BLAKE3 (ТЗ §8).</summary>
    public long SmallFileThreshold { get; init; } = 1L << 20;

    /// <summary>Size of each quick sample (ТЗ §9).</summary>
    public int SampleSize { get; init; } = 64 * 1024;

    /// <summary>Minimum size for the middle (50%) sample, stage Q3.</summary>
    public long MiddleSampleMinSize { get; init; } = 16L << 20;

    /// <summary>Minimum size for the 25%/75% samples, stage Q4.</summary>
    public long QuarterSamplesMinSize { get; init; } = 1L << 30;

    /// <summary>Streaming buffer for full hashing and byte comparison (ТЗ §10).</summary>
    public int StreamBufferSize { get; init; } = 1 << 20;

    /// <summary>Concurrent large sequential readers per storage domain (ТЗ §16).</summary>
    public int FullReadersHdd { get; init; } = 1;
    public int FullReadersSsd { get; init; } = 2;
    public int FullReadersNvme { get; init; } = 4;     // maximum; autotune starts at FullReadersNvmeStart
    public int FullReadersNvmeStart { get; init; } = 2;
    public bool AutotuneNvme { get; init; } = true;
    public int FullReadersNetwork { get; init; } = 1;
    public int FullReadersUnknown { get; init; } = 1;

    /// <summary>Concurrent small (sample / metadata) operations per storage domain.</summary>
    public int QuickReadersHdd { get; init; } = 1;
    public int QuickReadersSsd { get; init; } = 4;
    public int QuickReadersNvme { get; init; } = 8;
    public int QuickReadersNetwork { get; init; } = 2;
    public int QuickReadersUnknown { get; init; } = 1;

    /// <summary>Global CPU budget shared by all hashing workers (ТЗ §17).</summary>
    public int CpuBudget { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Version of the deterministic sampling scheme (stored with any future cache).</summary>
    public const int SamplingSchemeVersion = 1;

    public void Validate()
    {
        if (SmallFileThreshold < 0 || SampleSize <= 0 || SampleSize > (16 << 20) ||
            StreamBufferSize <= 0 || StreamBufferSize > (16 << 20) || MiddleSampleMinSize < 0 || QuarterSamplesMinSize < 0 || CpuBudget <= 0 ||
            new[] { FullReadersHdd, FullReadersSsd, FullReadersNvme, FullReadersNvmeStart, FullReadersNetwork,
                FullReadersUnknown, QuickReadersHdd, QuickReadersSsd, QuickReadersNvme, QuickReadersNetwork, QuickReadersUnknown }.Any(n => n <= 0))
            throw new ArgumentOutOfRangeException(nameof(Tuning), "Sizes and worker budgets must be positive and bounded");
    }

    public static Tuning Default { get; } = new();

    public int FullReaders(StorageKind k) => k switch
    {
        StorageKind.Hdd => FullReadersHdd,
        StorageKind.Ssd => FullReadersSsd,
        StorageKind.Nvme => FullReadersNvme,
        StorageKind.Network => FullReadersNetwork,
        _ => FullReadersUnknown,
    };

    /// <summary>Starting worker count for autotune (equal to the maximum where autotune is off).</summary>
    public int FullReadersStart(StorageKind k) =>
        k == StorageKind.Nvme && AutotuneNvme ? Math.Min(FullReadersNvmeStart, FullReadersNvme) : FullReaders(k);

    public int QuickReaders(StorageKind k) => k switch
    {
        StorageKind.Hdd => QuickReadersHdd,
        StorageKind.Ssd => QuickReadersSsd,
        StorageKind.Nvme => QuickReadersNvme,
        StorageKind.Network => QuickReadersNetwork,
        _ => QuickReadersUnknown,
    };
}
