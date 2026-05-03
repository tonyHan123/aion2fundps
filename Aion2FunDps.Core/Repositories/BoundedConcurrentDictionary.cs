using System.Collections.Concurrent;

namespace Aion2FunDps.Core.Repositories;

/// <summary>
/// ConcurrentDictionary wrapper with a hard cap on entry count. When a new
/// key would push count past the cap, the oldest entries (by insertion order)
/// are evicted in a small batch to amortize sweep cost.
///
/// This is FIFO eviction, not true LRU — chosen because:
///   - Entity / summon ids aren't queried after their owners despawn
///   - Recent inserts are recent activity, FIFO won't drop them
///   - True LRU would need a doubly-linked list under a lock, killing
///     the lock-free read path that ConcurrentDictionary provides
///
/// Cap exists to bound memory in long sessions (4+ hours in busy zones)
/// where the registries would otherwise accumulate tens of thousands of
/// stale ids — see project_release_prep_optimization.md item 3.
/// </summary>
public sealed class BoundedConcurrentDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<TKey, TValue> _dict = new();
    private readonly ConcurrentQueue<TKey> _insertionOrder = new();
    /// <summary>
    /// Approx count of unique inserts queued for FIFO eviction. Drifts
    /// slightly from <see cref="ConcurrentDictionary{TKey, TValue}.Count"/>
    /// when an evicted key was re-inserted in the same window — the queue
    /// can hold duplicates, but the dict resolves duplicates to the latest
    /// value. The drift is bounded by the eviction batch size.
    /// </summary>
    private int _orderSize;

    public BoundedConcurrentDictionary(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public TValue this[TKey key]
    {
        set => Set(key, value);
    }

    public void Set(TKey key, TValue value)
    {
        bool isNew = !_dict.ContainsKey(key);
        _dict[key] = value;
        if (!isNew) return;

        _insertionOrder.Enqueue(key);
        // Evict in batches of capacity/10 once over cap. Batching reduces
        // the cost of the dequeue loop by amortizing across inserts and
        // gives a stable count between cap and cap*1.1.
        int newSize = Interlocked.Increment(ref _orderSize);
        if (newSize <= _capacity) return;

        int batch = Math.Max(1, _capacity / 10);
        for (int i = 0; i < batch; i++)
        {
            if (!_insertionOrder.TryDequeue(out var oldKey)) break;
            Interlocked.Decrement(ref _orderSize);
            // Race: oldKey may have been re-inserted (and re-enqueued).
            // We just drop it — the next enqueue will keep it alive.
            // This is the "drift" mentioned in _orderSize's doc.
            _dict.TryRemove(oldKey, out _);
        }
    }

    public bool TryGetValue(TKey key, out TValue value) =>
        _dict.TryGetValue(key, out value!);

    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    public bool TryRemove(TKey key, out TValue value) =>
        _dict.TryRemove(key, out value!);

    public int Count => _dict.Count;
}
