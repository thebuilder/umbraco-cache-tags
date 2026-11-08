using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Keys;

/// <summary>
///     The site root a document is under: the top-level document of its tree, or the document itself at
///     the top. An Umbraco instance can hold several sites, one per root, and a listing confined to one
///     root's tree only changes when that root's documents do.
/// </summary>
internal sealed class SiteRoots(IIdKeyMap idKeyMap)
{
    /// <summary>
    ///     The root of a document with the given path (<c>-1,1069,1077</c>, root first), or <c>null</c>
    ///     for one in the recycle bin or a path that cannot be read.
    /// </summary>
    public Guid? OfPath(string? path)
        => RootId(path) is { } id && idKeyMap.GetKeyForId(id, UmbracoObjectTypes.Document) is { Success: true } attempt
            ? attempt.Result
            : null;

    /// <summary>The id of the root in a path, or <c>null</c> for the recycle bin (<c>-1,-20,…</c>) or a path that cannot be read.</summary>
    public static int? RootId(string? path)
    {
        var ids = path?.Split(',') ?? [];
        return ids.Length >= 2 && ids[0] == "-1" && int.TryParse(ids[1], out var id) && id > 0 ? id : null;
    }
}
