using System.IO.Hashing;

namespace FindCopy.Core;

/// <summary>Quick fingerprint (ТЗ §9). Only ever used to prove files differ.</summary>
public interface IQuickHasher
{
    ulong Hash(ReadOnlySpan<byte> data);
}

public sealed class Xxh3QuickHasher : IQuickHasher
{
    public static readonly Xxh3QuickHasher Instance = new();
    public ulong Hash(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);
}

/// <summary>Full content hash (ТЗ §10). Injected so tests can force collisions (ТЗ §27 #5).</summary>
public interface IFullHasher
{
    IFullHashState Create();
}

public interface IFullHashState : IDisposable
{
    void Update(ReadOnlySpan<byte> data);
    void Finalize(Span<byte> hash32);
}

/// <summary>BLAKE3-256 via the official Rust SIMD implementation (Blake3.NET), single-threaded per file.</summary>
public sealed class Blake3FullHasher : IFullHasher
{
    public static readonly Blake3FullHasher Instance = new();
    public IFullHashState Create() => new State();

    private sealed class State : IFullHashState
    {
        private Blake3.Hasher _h = Blake3.Hasher.New();
        public void Update(ReadOnlySpan<byte> data) => _h.Update(data);
        public void Finalize(Span<byte> hash32) => _h.Finalize(hash32);
        public void Dispose() => _h.Dispose();
    }
}
