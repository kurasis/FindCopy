using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace FindCopy.Core;

/// <summary>Retries a failed optimized listing without replaying entries already emitted.</summary>
public static class DirectoryEnumerationFallback
{
    public delegate FileStatus Enumerator(DirEntryHandler handler, out string? error);

    public static FileStatus Run(Enumerator optimized, Enumerator baseline, DirEntryHandler handler,
        CancellationToken ct, out bool usedFallback, out string? error)
    {
        // Names use an arena and value-type collision chains, not one string/list per entry.
        var names = new PathStore(chunkBits: 10);
        var heads = new Dictionary<ulong, int>();
        var entries = new List<(long Handle, int Length, int Next)>();
        bool Add(ReadOnlySpan<char> name)
        {
            ulong hash = XxHash3.HashToUInt64(MemoryMarshal.AsBytes(name));
            int previous = heads.TryGetValue(hash, out int head) ? head : -1;
            for (int i = previous; i >= 0; i = entries[i].Next)
                if (names.GetName(entries[i].Handle, entries[i].Length).SequenceEqual(name)) return false;
            heads[hash] = entries.Count;
            entries.Add((names.AddName(name), name.Length, previous));
            return true;
        }
        void Emit(ReadOnlySpan<char> name, in EntryInfo info)
        {
            ct.ThrowIfCancellationRequested();
            if (Add(name)) handler(name, info);
        }
        var status = optimized(Emit, out error);
        ct.ThrowIfCancellationRequested();
        usedFallback = status != FileStatus.Ok;
        return usedFallback ? baseline(Emit, out error) : status;
    }
}
