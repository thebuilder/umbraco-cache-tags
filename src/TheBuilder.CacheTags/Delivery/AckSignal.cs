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
/// <param name="Ancestors">
///     The items' ancestors. Umbraco refreshes a branch (a publish with descendants, a delete) as one
///     refresh of its root, which refreshes every item under it.
/// </param>
internal sealed record AckSignal(string Kind, IReadOnlyList<Guid> Keys, string Requires, IReadOnlyList<Guid>? Ancestors = null)
{
    public const string Content = "content";
    public const string Media = "media";
    public const string Types = "types";
    public const string Everything = "everything";

    /// <summary>A custom change that waits for nothing: it is released at once.</summary>
    public const string None = "none";

    /// <summary>The kind of a custom change's signal: this, then the signal's name.</summary>
    public const string CustomPrefix = "custom:";

    public const string Publish = "publish";
    public const string Unpublish = "unpublish";
    public const string Branch = "branch";
    public const string Remove = "remove";
    public const string Any = "any";

    public static AckSignal For(Change change)
    {
        var keys = change.Items.Select(item => item.Key).ToList();
        var ancestors = change.Items.SelectMany(item => item.Ancestors ?? []).Distinct().ToList();
        return change.Kind switch
        {
            ChangeKind.Published => new(Content, keys, Publish, ancestors),
            ChangeKind.Unpublished => new(Content, keys, Unpublish, ancestors),
            ChangeKind.Moved or ChangeKind.Trashed => new(Content, keys, Branch, ancestors),
            ChangeKind.Deleted => new(Content, keys, Remove, ancestors),
            ChangeKind.Sorted => new(Content, keys, Any),
            ChangeKind.MediaSaved or ChangeKind.MediaMoved or ChangeKind.MediaTrashed or ChangeKind.MediaDeleted => new(Media, keys, Any, ancestors),
            ChangeKind.TypesChanged => new(Types, [], Any),
            ChangeKind.Everything => new(Everything, [], Any),
            ChangeKind.Custom => new(change.WaitFor is null ? None : CustomPrefix + change.WaitFor, [], Any),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "A change kind needs an ack signal."),
        };
    }

    /// <summary>Whether servers must acknowledge the change before it is released.</summary>
    public bool WaitsForServers => Kind != None;

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
            return Ancestors?.Contains(key) is true && IsBranch(refresh);
        }

        // A refresh of everything (no key) covers every item in any way.
        if (refresh.Key is null || Requires == Any)
        {
            return true;
        }

        return Requires switch
        {
            Publish => refresh.HasPublishedCultures || refresh.HasUnpublishedCultures,
            // Deleting a published item unpublishes it, refreshed as a removal.
            Unpublish => refresh.HasUnpublishedCultures || refresh.ChangeTypes.HasFlag(TreeChangeTypes.Remove),
            Branch => IsBranch(refresh),
            Remove => refresh.ChangeTypes.HasFlag(TreeChangeTypes.Remove),
            _ => false,
        };
    }

    private static bool IsBranch(ObservedRefresh refresh)
        => refresh.ChangeTypes.HasFlag(TreeChangeTypes.RefreshBranch) || refresh.ChangeTypes.HasFlag(TreeChangeTypes.Remove);
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
