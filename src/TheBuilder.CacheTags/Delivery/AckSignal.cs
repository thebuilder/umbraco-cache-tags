using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Services.Changes;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     What a server's cache refresh must show for that server to acknowledge a change: it has
///     refreshed one of the change's items in the way the change refreshes them. The precise
///     requirement keeps an unrelated refresh of the same item (a draft save, say) from counting.
/// </summary>
/// <param name="Kind">Which caches: content, media, types or everything.</param>
/// <param name="Keys">The items, for content and media.</param>
/// <param name="Requires">For content: publish, unpublish, branch, remove or any.</param>
internal sealed record AckSignal(string Kind, IReadOnlyList<Guid> Keys, string Requires)
{
    public const string Content = "content";
    public const string Media = "media";
    public const string Types = "types";
    public const string Everything = "everything";

    public const string Publish = "publish";
    public const string Unpublish = "unpublish";
    public const string Branch = "branch";
    public const string Remove = "remove";
    public const string Any = "any";

    public static AckSignal For(Change change)
    {
        var keys = change.Items.Select(item => item.Key).ToList();
        return change.Kind switch
        {
            ChangeKind.Published => new(Content, keys, Publish),
            ChangeKind.Unpublished => new(Content, keys, Unpublish),
            ChangeKind.Moved or ChangeKind.Trashed => new(Content, keys, Branch),
            ChangeKind.Deleted => new(Content, keys, Remove),
            ChangeKind.Sorted => new(Content, keys, Any),
            ChangeKind.MediaSaved or ChangeKind.MediaMoved or ChangeKind.MediaTrashed or ChangeKind.MediaDeleted => new(Media, keys, Any),
            ChangeKind.TypesChanged => new(Types, [], Any),
            _ => new(Everything, [], Any),
        };
    }

    /// <summary>Whether a refresh this server just applied acknowledges the change.</summary>
    public bool IsMetBy(ObservedRefresh refresh)
    {
        if (refresh.Kind != Kind)
        {
            return false;
        }

        if (Kind is Types or Everything)
        {
            return true;
        }

        if (refresh.Key is { } key && Keys.Contains(key) is false)
        {
            return false;
        }

        // A refresh of everything (no key) covers every item in any way.
        if (refresh.Key is null || Requires == Any)
        {
            return true;
        }

        return Requires switch
        {
            Publish => refresh.HasPublishedCultures || refresh.HasUnpublishedCultures,
            Unpublish => refresh.HasUnpublishedCultures,
            Branch => refresh.ChangeTypes.HasFlag(TreeChangeTypes.RefreshBranch) || refresh.ChangeTypes.HasFlag(TreeChangeTypes.Remove),
            Remove => refresh.ChangeTypes.HasFlag(TreeChangeTypes.Remove),
            _ => false,
        };
    }
}

/// <summary>
///     One cache refresh a server applied, from a cache refresher notification.
/// </summary>
/// <param name="Kind">Which caches (see <see cref="AckSignal" />).</param>
/// <param name="Key">The item refreshed, or <c>null</c> when everything of that kind was.</param>
/// <param name="ChangeTypes">How the item was refreshed (content and media).</param>
/// <param name="HasPublishedCultures">Whether the refresh carried published cultures (a publish).</param>
/// <param name="HasUnpublishedCultures">Whether it carried unpublished cultures (an unpublish).</param>
internal sealed record ObservedRefresh(string Kind, Guid? Key, TreeChangeTypes ChangeTypes = TreeChangeTypes.None, bool HasPublishedCultures = false, bool HasUnpublishedCultures = false);
