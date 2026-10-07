using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FindCopy.Bench;

/// <summary>Generates reproducible workloads with bounded memory and 64-bit lengths.</summary>
public static class DatasetGenerator
{
    private const int BufferSize = 1 << 20;

    public static string Generate(string scenario, string directory, int scale, long sizeOverride = 0,
        int countOverride = 0, int copies = 4, bool sparse = false)
    {
        if (scale <= 0 || sizeOverride < 0) throw new ArgumentOutOfRangeException(nameof(scale));
        string marker = directory + ".generated";
        if (new FileInfo(marker).LinkTarget != null ||
            (File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Refusing a linked dataset marker");
        if (countOverride < 0 || copies < 2) throw new ArgumentOutOfRangeException(nameof(countOverride));
        string version = $"v3:{scenario}:{scale}:{sizeOverride}:{countOverride}:{copies}:{sparse}";
        if (Directory.Exists(directory) && File.Exists(marker) && File.ReadAllText(marker) == version) return directory;
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Refusing to overwrite an unrecognized or incomplete dataset: " + directory);
        Directory.CreateDirectory(directory);
        Console.WriteLine($"Generating scenario {scenario} in {directory} ...");
        var random = new Random(12345);
        byte[] pattern = new byte[BufferSize];
        random.NextBytes(pattern);
        switch (scenario)
        {
            case "A":
                int count = countOverride > 0 ? countOverride : checked(20_000 * scale);
                for (int i = 1; i <= count; i++)
                {
                    string sub = Path.Combine(directory, (i % 100).ToString("00"));
                    if (i <= 100) Directory.CreateDirectory(sub);
                    CreateSparseFile(Path.Combine(sub, $"f{i}.bin"), i);
                }
                break;
            case "B":
                Array.Clear(pattern);
                for (int i = 0; i < (countOverride > 0 ? countOverride : checked(200 * scale)); i++)
                {
                    random.NextBytes(pattern.AsSpan(0, 4096));
                    string path = Path.Combine(directory, $"b{i}.bin");
                    CreateSparseFile(path, 3L * BufferSize / 2);
                    using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Write);
                    RandomAccess.Write(h, pattern.AsSpan(0, 4096), 0);
                }
                break;
            case "C":
            case "D":
                long size = sizeOverride > 0 ? sizeOverride : checked((scenario == "C" ? 64L : 256L) * BufferSize * scale);
                if (size < 65536) throw new ArgumentOutOfRangeException(nameof(sizeOverride));
                for (int i = 0; i < copies; i++)
                {
                    string path = Path.Combine(directory, $"{scenario.ToLowerInvariant()}{i}.bin");
                    if (sparse)
                    {
                        CreateSparseFile(path, size);
                        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write);
                        RandomAccess.Write(handle, pattern.AsSpan(0, 65536), 0);
                    }
                    else WritePatternFile(path, size, pattern);
                    if (scenario == "C")
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
                        stream.Position = size / 2 + 100;
                        byte[] difference = BitConverter.GetBytes((long)i);
                        stream.Write(difference);
                    }
                }
                break;
            default:
                throw new ArgumentException("Unknown scenario: " + scenario);
        }
        File.WriteAllText(marker, version);
        return directory;
    }

    public static void WritePatternFile(string path, long size, byte[] pattern)
    {
        if (size < 0 || pattern.Length == 0) throw new ArgumentOutOfRangeException(nameof(size));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
        for (long offset = 0; offset < size;)
        {
            int length = (int)Math.Min(pattern.Length, size - offset);
            stream.Write(pattern.AsSpan(0, length));
            offset += length;
        }
    }

    public static void CreateSparseFile(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        if (OperatingSystem.IsWindows() && !DeviceIoControl(stream.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Sparse files are not supported by the selected filesystem");
        stream.SetLength(size);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
