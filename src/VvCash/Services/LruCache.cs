using System;
using System.Collections.Generic;

namespace VvCash.Services;

/// <summary>A bounded most-recently-used-wins map.
///
/// Dictionary plus LinkedList under a lock rather than ConcurrentDictionary: LRU needs
/// an order, and ConcurrentDictionary does not have one. The lock is cheap to hold: the
/// critical section is a dictionary lookup and at most two list splices, nothing more —
/// see GetOrAdd's own doc for what that requires of the factory you pass it.
///
/// Eviction drops the reference and nothing else. It deliberately does NOT dispose the
/// evicted value: for the image cache that value is a Bitmap which a visible row may
/// still be bound to, and disposing it under a live binding is a worse bug than the
/// unbounded growth this class exists to fix. Freeing is the GC's job, once nothing
/// holds it.</summary>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                Touch(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    /// <summary>The stored value for <paramref name="key"/>, calling
    /// <paramref name="factory"/> only when there is none. The factory runs under the
    /// lock, so it must return without blocking — the image loader's factory starts a
    /// Task and returns it, it does not await one — and it must not call back into this
    /// same cache. C#'s lock is re-entrant, so a factory that re-enters would not
    /// deadlock; it would run its own insert or evict while this call is still mid-insert,
    /// orphan a node in <c>_order</c> that <c>_map</c> no longer points to, and quietly
    /// reintroduce the unbounded growth this class exists to prevent.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                Touch(existing);
                return existing.Value.Value;
            }

            var created = factory(key);
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, created));
            _order.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            return created;
        }
    }

    /// <summary>Replaces the value for a key that is already present, leaving it newest.
    /// Adds it if it is absent.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _map.Remove(key);
            }
            var fresh = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, value));
            _order.AddFirst(fresh);
            _map[key] = fresh;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    private void Touch(LinkedListNode<KeyValuePair<TKey, TValue>> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
    }
}
