using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     The content type, media type and language names that exist, for telling a request that names
///     real ones from one carrying input that can vary without end. Read at most once a minute, so a
///     request does not open a database scope per name. A name created since is taken for unknown (its
///     responses carry their keys themselves, which only over-purges) and one deleted since for known
///     until the next read.
/// </summary>
internal sealed class KnownNames(IContentTypeService contentTypes, IMediaTypeService mediaTypes, ILanguageService languages, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private volatile Snapshot? _snapshot;

    /// <summary>Whether a content type (or, with <paramref name="media" />, a media type) has the alias, ignoring case.</summary>
    public bool IsType(string alias, bool media) => (media ? Current().MediaTypes : Current().ContentTypes).Contains(alias);

    /// <summary>Whether a language has the ISO code, ignoring case.</summary>
    public bool IsCulture(string culture) => Current().Cultures.Contains(culture);

    private Snapshot Current() => _snapshot is { } current && time.GetUtcNow() < current.ExpiresUtc ? current : Read();

    private Snapshot Read()
    {
        var snapshot = new Snapshot(
            contentTypes.GetAll().Select(type => type.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase),
            mediaTypes.GetAll().Select(type => type.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase),
            languages.GetAllAsync().GetAwaiter().GetResult().Select(language => language.IsoCode).ToHashSet(StringComparer.OrdinalIgnoreCase),
            time.GetUtcNow() + Lifetime);
        _snapshot = snapshot;
        return snapshot;
    }

    private sealed record Snapshot(HashSet<string> ContentTypes, HashSet<string> MediaTypes, HashSet<string> Cultures, DateTimeOffset ExpiresUtc);
}
