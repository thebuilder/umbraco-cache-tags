using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     The content type and media type aliases that exist, for telling a listing filtered by real types
///     from one carrying input that can vary without end. Read at most once a minute, so a request does
///     not open a database scope per name: once they are older, one caller reads them again in the
///     background while every request goes on with the ones it has. A type created since is taken for
///     unknown (its listings carry the catch-all search- key, which every publish purges) and one
///     deleted since for known until the next read.
/// </summary>
internal sealed class KnownNames(IContentTypeService contentTypes, IMediaTypeService mediaTypes, TimeProvider time, ILogger<KnownNames> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly Lock _firstRead = new();
    private volatile Snapshot? _snapshot;
    private int _reading;

    /// <summary>Whether a content type (or, with <paramref name="media" />, a media type) has the alias, ignoring case.</summary>
    public bool IsType(string alias, bool media) => (media ? Current().MediaTypes : Current().ContentTypes).Contains(alias);

    private Snapshot Current()
    {
        if (_snapshot is { } current)
        {
            if (time.GetUtcNow() >= current.ExpiresUtc && Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
            {
                _ = Task.Run(ReadAgain);
            }

            return current;
        }

        lock (_firstRead)
        {
            return _snapshot ?? Read();
        }
    }

    private void ReadAgain()
    {
        try
        {
            Read();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Reading the content types and media types failed; the ones read before are used until the next try.");
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private Snapshot Read()
    {
        var snapshot = new Snapshot(
            contentTypes.GetAll().Select(type => type.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase),
            mediaTypes.GetAll().Select(type => type.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase),
            time.GetUtcNow() + Lifetime);
        _snapshot = snapshot;
        return snapshot;
    }

    private sealed record Snapshot(HashSet<string> ContentTypes, HashSet<string> MediaTypes, DateTimeOffset ExpiresUtc);
}
