using System.Linq;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class ProductImageCacheTest
{
    [Fact]
    public void GetOrAdd_PastTheCap_EvictsTheLeastRecentlyUsed()
    {
        var cache = new LruCache<string, string>(capacity: 3);

        cache.GetOrAdd("a", _ => "A");
        cache.GetOrAdd("b", _ => "B");
        cache.GetOrAdd("c", _ => "C");
        cache.GetOrAdd("d", _ => "D");

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void GetOrAdd_TouchingAnEntry_MakesItTheNewest()
    {
        var cache = new LruCache<string, string>(capacity: 3);

        cache.GetOrAdd("a", _ => "A");
        cache.GetOrAdd("b", _ => "B");
        cache.GetOrAdd("c", _ => "C");
        cache.GetOrAdd("a", _ => "A2");   // touch, not replace
        cache.GetOrAdd("d", _ => "D");

        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("A", a);              // the factory did not run again
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Set_OnAnExistingKey_ReplacesTheValueAndLeavesItNewest()
    {
        var cache = new LruCache<string, string>(capacity: 3);

        cache.GetOrAdd("a", _ => "A");
        cache.GetOrAdd("b", _ => "B");
        cache.GetOrAdd("c", _ => "C");     // full: oldest to newest is a, b, c

        cache.Set("a", "A2");              // overwrite the oldest key
        cache.GetOrAdd("d", _ => "D");     // one more; if Set made "a" newest, "b" goes next

        Assert.Equal(3, cache.Count);
        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("A2", a);             // Set replaced the value
        Assert.False(cache.TryGet("b", out _));  // next-oldest evicted, not "a"
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    /// <summary>Eviction must not dispose. The value a register evicts may be a Bitmap
    /// that a visible row is bound to right now; dropping the reference is the fix,
    /// disposing it is a blank tile or worse.</summary>
    [Fact]
    public void Eviction_HandsBackAValueThatIsStillUsable()
    {
        var cache = new LruCache<string, Probe>(capacity: 1);
        var first = cache.GetOrAdd("a", _ => new Probe());

        cache.GetOrAdd("b", _ => new Probe());

        Assert.False(cache.TryGet("a", out _));
        Assert.False(first.Disposed);
    }

    private sealed class Probe : System.IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
