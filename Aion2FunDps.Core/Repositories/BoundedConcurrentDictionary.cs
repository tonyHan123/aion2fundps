using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
///
/// Pin/Unpin: keys can be marked as un-evictable. The eviction loop
/// re-enqueues pinned keys instead of dropping them, so they stay in the
/// dict regardless of how many fresh inserts pile up. Used by
/// NicknameRegistry to prevent the user's own canonical entry from being
/// dropped during long sessions in busy zones.
/// </summary>
public sealed class BoundedConcurrentDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<TKey, TValue> _dict;
    private readonly ConcurrentDictionary<TKey, byte> _pinned;
    private readonly ConcurrentQueue<TKey> _insertionOrder = new();
    /// <summary>
    /// Approx count of unique inserts queued for FIFO eviction. Drifts
    /// slightly from <see cref="ConcurrentDictionary{TKey, TValue}.Count"/>
    /// when an evicted key was re-inserted in the same window — the queue
    /// can hold duplicates, but the dict resolves duplicates to the latest
    /// value. The drift is bounded by the eviction batch size.
    /// </summary>
    private int _orderSize;

    public BoundedConcurrentDictionary(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _dict   = comparer is null ? new ConcurrentDictionary<TKey, TValue>() : new ConcurrentDictionary<TKey, TValue>(comparer);
        _pinned = comparer is null ? new ConcurrentDictionary<TKey, byte>()   : new ConcurrentDictionary<TKey, byte>(comparer);
    }

    /// <summary>Mark a key as un-evictable. Idempotent — safe to call repeatedly.</summary>
    public void Pin(TKey key) => _pinned[key] = 0;

    /// <summary>Remove the un-evictable mark. The key is then subject to FIFO
    /// eviction like any other.</summary>
    public void Unpin(TKey key) => _pinned.TryRemove(key, out _);

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
        int evicted = 0;
        // Loop until we've evicted `batch` non-pinned keys or the queue
        // empties. Pinned keys (e.g., the user's own canonical) are
        // re-enqueued so they continue rotating through the queue without
        // being dropped from the dict.
        int safety = batch * 4;  // bound the worst case when most keys are pinned
        while (evicted < batch && safety-- > 0
               && _insertionOrder.TryDequeue(out var oldKey))
        {
            Interlocked.Decrement(ref _orderSize);
            if (_pinned.ContainsKey(oldKey))
            {
                _insertionOrder.Enqueue(oldKey);
                Interlocked.Increment(ref _orderSize);
                continue;
            }
            // Race: oldKey may have been re-inserted (and re-enqueued).
            // We just drop it — the next enqueue will keep it alive.
            // This is the "drift" mentioned in _orderSize's doc.
            _dict.TryRemove(oldKey, out _);
            evicted++;
        }
    }

    public bool TryGetValue(TKey key, out TValue value) =>
        _dict.TryGetValue(key, out value!);

    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    public bool TryRemove(TKey key, out TValue value) =>
        _dict.TryRemove(key, out value!);

    public int Count => _dict.Count;

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dict.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
