using Microsoft.Extensions.Caching.Memory;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     What one notification of an operation leaves for a later one. Umbraco does not always pass the
///     same notification state from the "-ing" to the "-ed" notification (a publish with descendants
///     gives the published notification a fresh one), so this is kept per item instead, briefly.
/// </summary>
internal sealed class PendingOperations : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    // Short: a mark the sort's publish did not take must not swallow a real publish later.
    private static readonly TimeSpan SortingLifetime = TimeSpan.FromSeconds(30);

    private readonly MemoryCache _items = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    /// <summary>Keeps an item's live URL segments by culture, before it is published.</summary>
    public void RememberSegments(Guid key, IReadOnlyDictionary<string, string?> segments)
        => _items.Set(("segments", key), segments, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });

    /// <summary>The live URL segments kept for an item before its publish, once.</summary>
    public IReadOnlyDictionary<string, string?>? TakeSegments(Guid key) => Take<IReadOnlyDictionary<string, string?>>(("segments", key));

    /// <summary>Keeps a content or media type's alias before it is renamed.</summary>
    public void RememberAlias(Guid key, string alias)
        => _items.Set(("alias", key), alias, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });

    /// <summary>The alias a type had before it was renamed, once.</summary>
    public string? TakeAlias(Guid key) => Take<string>(("alias", key));

    /// <summary>Marks published items being sorted: Umbraco publishes them again, which is not a change of their own.</summary>
    public void MarkSorting(IEnumerable<Guid> keys)
    {
        foreach (Guid key in keys)
        {
            _items.Set(("sorting", key), true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = SortingLifetime });
        }
    }

    /// <summary>Whether an item's publish is the sort's, once.</summary>
    public bool TakeSorting(Guid key) => Take<object>(("sorting", key)) is not null;

    public void Dispose() => _items.Dispose();

    private T? Take<T>(object key)
        where T : class
    {
        if (_items.TryGetValue(key, out var value) is false)
        {
            return null;
        }

        _items.Remove(key);
        return value as T;
    }
}
