using System.Buffers;
using System.Collections.Concurrent;

namespace FindCopy.Core;

/// <summary>Registry of storage domains (ТЗ §16): volumes of one physical disk share a domain.</summary>
public sealed class StorageDomains
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _byKey = new();
    private readonly List<StorageProfile> _profiles = new();

    public int GetOrAdd(StorageProfile p)
    {
        lock (_lock)
        {
            if (_byKey.TryGetValue(p.DomainKey, out int id)) return id;
            _profiles.Add(p);
            _byKey[p.DomainKey] = _profiles.Count - 1;
            return _profiles.Count - 1;
        }
    }

    public StorageProfile this[int id] { get { lock (_lock) return _profiles[id]; } }
    public IReadOnlyList<StorageProfile> All { get { lock (_lock) return _profiles.ToArray(); } }
}

/// <summary>
/// Storage-aware scheduler (ТЗ §16, §17): per-domain worker count chosen from the storage
/// kind (never "one thread per CPU core per disk"), plus one global CPU budget. Each worker
/// owns a fixed buffer, so memory does not depend on file sizes.
/// </summary>
public sealed class IoScheduler
{
    private readonly StorageDomains _domains;
    private readonly SemaphoreSlim _cpu;

    public IoScheduler(StorageDomains domains, int cpuBudget)
    {
        _domains = domains;
        _cpu = new SemaphoreSlim(Math.Max(1, cpuBudget));
    }

    public delegate void WorkItem(int item, byte[] buffer, byte[] buffer2);

    private sealed class DomainRun
    {
        public required int Id;
        public required ConcurrentQueue<int> Queue;
        public int Target;
        public int Max;
        public int Spawned;
        public long BytesDone;
        // autotune state
        public bool Tuning;
        public long WindowBytes;
        public long WindowStartTicks;
        public double LastRate;
        public bool LastWasIncrease;
    }

    /// <summary>Called when autotune settles on a worker count for a domain.</summary>
    public Action<int, int>? AutotuneSettled { get; set; }

    /// <param name="items">Work items, already in the preferred read order.</param>
    /// <param name="domainOf">Storage domain of an item.</param>
    /// <param name="concurrencyFor">Maximum workers per domain for a storage kind.</param>
    /// <param name="autotuneStartFor">
    /// Optional starting worker count. When it is below the maximum, the scheduler adds workers one
    /// at a time while throughput keeps improving by more than 10% (ТЗ §16: NVMe autotune 2–4).
    /// </param>
    /// <param name="itemBytes">Bytes an item reads, used to measure throughput for autotune.</param>
    public void Run(IReadOnlyList<int> items, Func<int, int> domainOf, Func<StorageKind, int> concurrencyFor,
        int bufferSize, bool twoBuffers, WorkItem work, CancellationToken ct,
        Func<StorageKind, int>? autotuneStartFor = null, Func<int, long>? itemBytes = null)
    {
        if (items.Count == 0) return;
        var domains = new Dictionary<int, DomainRun>();
        foreach (int it in items)
        {
            int d = domainOf(it);
            if (!domains.TryGetValue(d, out var dr))
                domains[d] = dr = new DomainRun { Id = d, Queue = new ConcurrentQueue<int>() };
            dr.Queue.Enqueue(it);
        }

        var threads = new List<Thread>();
        var errors = new ConcurrentQueue<Exception>();

        void Spawn(DomainRun dr)
        {
            int index = dr.Spawned++;
            var t = new Thread(() =>
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(bufferSize);
                byte[] buf2 = twoBuffers ? ArrayPool<byte>.Shared.Rent(bufferSize) : Array.Empty<byte>();
                try
                {
                    while (!ct.IsCancellationRequested && index < Volatile.Read(ref dr.Target) && dr.Queue.TryDequeue(out int item))
                    {
                        _cpu.Wait(ct);
                        try { work(item, buf, buf2); }
                        finally { _cpu.Release(); }
                        if (itemBytes != null) Interlocked.Add(ref dr.BytesDone, itemBytes(item));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Enqueue(ex); }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    if (twoBuffers) ArrayPool<byte>.Shared.Return(buf2);
                }
            })
            { IsBackground = true, Name = $"FindCopy IO d{dr.Id}#{index}", Priority = ThreadPriority.BelowNormal };
            lock (threads) threads.Add(t);
            t.Start();
        }

        foreach (var dr in domains.Values)
        {
            var kind = _domains[dr.Id].Kind;
            dr.Max = Math.Max(1, concurrencyFor(kind));
            int start = Math.Clamp(autotuneStartFor?.Invoke(kind) ?? dr.Max, 1, dr.Max);
            dr.Tuning = itemBytes != null && start < dr.Max;
            dr.Target = Math.Min(start, Math.Max(1, dr.Queue.Count));
            dr.WindowStartTicks = DateTime.UtcNow.Ticks;
            for (int i = 0; i < dr.Target; i++) Spawn(dr);
        }

        while (true)
        {
            Thread[] snapshot;
            lock (threads) snapshot = threads.ToArray();
            if (snapshot.All(t => !t.IsAlive)) break;
            snapshot.FirstOrDefault(t => t.IsAlive)?.Join(250);
            foreach (var dr in domains.Values.Where(d => d.Tuning))
                Autotune(dr, Spawn);
        }
        ct.ThrowIfCancellationRequested();
        if (!errors.IsEmpty) throw new AggregateException(errors);
    }

    private void Autotune(DomainRun dr, Action<DomainRun> spawn)
    {
        long now = DateTime.UtcNow.Ticks;
        double seconds = (now - dr.WindowStartTicks) / (double)TimeSpan.TicksPerSecond;
        long bytes = Interlocked.Read(ref dr.BytesDone) - dr.WindowBytes;
        if (seconds < 1.5 || bytes < (32L << 20)) return;
        double rate = bytes / seconds;
        dr.WindowBytes += bytes;
        dr.WindowStartTicks = now;

        if (dr.Queue.IsEmpty) { Settle(dr); return; }
        if (dr.LastRate == 0 || (dr.LastWasIncrease && rate > dr.LastRate * 1.10))
        {
            dr.LastRate = rate;
            if (dr.Target < dr.Max)
            {
                Volatile.Write(ref dr.Target, dr.Target + 1);
                dr.LastWasIncrease = true;
                if (dr.Spawned < dr.Target) spawn(dr);
                return;
            }
            Settle(dr);
            return;
        }
        // The last extra worker did not help: drop back (it exits after its current file).
        if (dr.LastWasIncrease) Volatile.Write(ref dr.Target, dr.Target - 1);
        Settle(dr);
    }

    private void Settle(DomainRun dr)
    {
        dr.Tuning = false;
        AutotuneSettled?.Invoke(dr.Id, dr.Target);
    }
}
