using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags;

/// <summary>
///     The keys the current response covers, for an action with <see cref="CacheTaggedAttribute" />.
///     Scoped to the request.
/// </summary>
public interface ICacheTagsResponse
{
    /// <summary>
    ///     Adds keys the response covers: custom keys (<c>x-</c> and lowercase letters, digits and
    ///     hyphens) for the site's own data, or <c>content-</c> and <c>media-</c> keys of items it shows.
    /// </summary>
    /// <exception cref="ArgumentException">A key is not one a site may name.</exception>
    void Add(params string[] keys);
}

internal sealed class CacheTagsResponse : ICacheTagsResponse
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Keys => _keys;

    public void Add(params string[] keys)
    {
        if (keys.FirstOrDefault(key => CacheKeys.IsNameable(key) is false || key == CacheKeys.All) is { } invalid)
        {
            throw new ArgumentException(
                $"\"{invalid}\" is not a key a response may name: use x- and lowercase letters, digits and hyphens (at most {CacheKeys.MaxCustomKeyLength} characters), or an item's content- or media- key.",
                nameof(keys));
        }

        _keys.UnionWith(keys);
    }
}
