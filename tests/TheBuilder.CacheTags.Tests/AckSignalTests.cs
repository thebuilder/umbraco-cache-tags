using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Services.Changes;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Which cache refreshes acknowledge which changes. Payload shapes are the ones the spike recorded (spike/FINDINGS.md, section 4).</summary>
public sealed class AckSignalTests
{
    private static readonly Guid A = new("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("10000000-0000-0000-0000-00000000000b");

    private static AckSignal Signal(string kind)
        => AckSignal.For(new Change(Guid.Empty, DateTime.UnixEpoch, Enum.Parse<ChangeKind>(kind), [new ChangedItem(A, "page", Guid.Empty)], [], null));

    private static ObservedRefresh Content(Guid key, TreeChangeTypes types, bool published = false, bool unpublished = false)
        => new(AckSignal.Content, key, types, published, unpublished);

    [Fact]
    public void A_publish_is_acknowledged_by_the_refresh_that_carries_published_cultures_not_the_draft_save_before_it()
    {
        AckSignal publish = Signal(nameof(ChangeKind.Published));

        Assert.False(publish.IsMetBy(Content(A, TreeChangeTypes.RefreshNode)));
        Assert.True(publish.IsMetBy(Content(A, TreeChangeTypes.RefreshNode, published: true)));
        Assert.True(publish.IsMetBy(Content(A, TreeChangeTypes.RefreshNode, unpublished: true)));
    }

    [Fact]
    public void A_refresh_of_another_item_does_not_count()
    {
        Assert.False(Signal(nameof(ChangeKind.Published)).IsMetBy(Content(B, TreeChangeTypes.RefreshNode, published: true)));
    }

    [Fact]
    public void An_unpublish_needs_unpublished_cultures()
    {
        AckSignal unpublish = Signal(nameof(ChangeKind.Unpublished));

        Assert.False(unpublish.IsMetBy(Content(A, TreeChangeTypes.RefreshBranch, published: true)));
        Assert.True(unpublish.IsMetBy(Content(A, TreeChangeTypes.RefreshBranch, unpublished: true)));
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Moved))]
    [InlineData(nameof(ChangeKind.Trashed))]
    public void Moves_and_trashing_need_a_branch_refresh(string kind)
    {
        Assert.False(Signal(kind).IsMetBy(Content(A, TreeChangeTypes.RefreshNode)));
        Assert.True(Signal(kind).IsMetBy(Content(A, TreeChangeTypes.RefreshBranch)));
    }

    [Fact]
    public void A_refresh_of_everything_acknowledges_any_content_change()
    {
        Assert.True(Signal(nameof(ChangeKind.Published)).IsMetBy(new ObservedRefresh(AckSignal.Content, null, TreeChangeTypes.RefreshAll)));
    }

    [Fact]
    public void Media_changes_need_a_media_refresh_of_the_item()
    {
        AckSignal media = Signal(nameof(ChangeKind.MediaSaved));

        Assert.False(media.IsMetBy(Content(A, TreeChangeTypes.RefreshNode, published: true)));
        Assert.True(media.IsMetBy(new ObservedRefresh(AckSignal.Media, A, TreeChangeTypes.RefreshNode)));
    }

    [Fact]
    public void Type_and_language_changes_are_acknowledged_by_their_own_refreshers()
    {
        AckSignal types = AckSignal.For(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.TypesChanged, [], ["page"], null));
        AckSignal everything = AckSignal.For(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Everything, [], [], null));

        Assert.True(types.IsMetBy(new ObservedRefresh(AckSignal.Types, null)));
        Assert.False(types.IsMetBy(new ObservedRefresh(AckSignal.Everything, null)));
        Assert.True(everything.IsMetBy(new ObservedRefresh(AckSignal.Everything, null)));
    }
}
