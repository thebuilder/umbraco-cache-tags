using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

public sealed class PurgePlannerTests
{
    private static readonly Guid Site = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid C = new("10000000-0000-0000-0000-00000000000c");
    private static readonly Guid Child = new("10000000-0000-0000-0000-000000000013");
    private static readonly Guid Grandchild = new("10000000-0000-0000-0000-000000000014");
    private static readonly Guid Image = new("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Folder = new("20000000-0000-0000-0000-0000000000ff");

    private sealed class Tree : IDescendants
    {
        public IEnumerable<Guid> Of(Guid key) => key == C ? [Child, Grandchild] : [];
    }

    private static PurgePlan Plan(ChangeKind kind, ChangedItem item, int budget = 500, IReadOnlyList<string>? cultures = null)
        => new PurgePlanner(new Tree()).Plan(new Change(Guid.Empty, DateTime.UnixEpoch, kind, [item], [], cultures), budget);

    [Fact]
    public void A_publish_reaches_the_item_its_parents_listings_its_type_and_the_index()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "spikePage", Site), cultures: ["da-DK"]);

        Assert.False(plan.Everything);
        Assert.Equal(["da-DK"], plan.Cultures);
        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-000000000001",
                "content-10000000-0000-0000-0000-00000000000c",
                "search-deliveryapicontentindex",
                "type-spikepage",
            ],
            plan.Keys);
    }

    [Fact]
    public void A_publish_that_changes_the_route_reaches_every_descendant()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "spikePage", Site, RouteChanged: true));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
        Assert.Contains("content-10000000-0000-0000-0000-000000000014", plan.Keys);
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Unpublished))]
    [InlineData(nameof(ChangeKind.Trashed))]
    [InlineData(nameof(ChangeKind.Moved))]
    public void Unpublishing_trashing_and_moving_always_reach_the_descendants(string kind)
    {
        PurgePlan plan = Plan(Enum.Parse<ChangeKind>(kind), new ChangedItem(C, "spikePage", Site));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
    }

    [Fact]
    public void A_move_reaches_the_listings_of_both_parents()
    {
        PurgePlan plan = Plan(ChangeKind.Moved, new ChangedItem(Child, "spikePage", C, OldParentKey: Site));

        Assert.Contains("children-10000000-0000-0000-0000-00000000000c", plan.Keys);
        Assert.Contains("children-10000000-0000-0000-0000-000000000001", plan.Keys);
    }

    [Fact]
    public void Too_many_descendants_purge_everything()
    {
        PurgePlan plan = Plan(ChangeKind.Unpublished, new ChangedItem(C, "spikePage", Site), budget: 1);

        Assert.True(plan.Everything);
        Assert.Empty(plan.Keys);
    }

    [Fact]
    public void A_sort_reaches_only_the_parents_listings()
    {
        PurgePlan plan = Plan(ChangeKind.Sorted, new ChangedItem(Child, "spikePage", C));

        Assert.Equal(["children-10000000-0000-0000-0000-00000000000c"], plan.Keys);
    }

    [Fact]
    public void A_media_save_reaches_the_media_item_and_media_listings_but_not_descendants()
    {
        PurgePlan plan = Plan(ChangeKind.MediaSaved, new ChangedItem(Image, "Image", Folder, RouteChanged: true));

        Assert.Equal(
            [
                "children-20000000-0000-0000-0000-0000000000ff",
                "media-20000000-0000-0000-0000-000000000001",
                "search-media",
                "type-image",
            ],
            plan.Keys);
    }

    [Fact]
    public void A_type_change_reaches_each_types_listings_and_every_response_rendering_it()
    {
        PurgePlan plan = new PurgePlanner(new Tree()).Plan(
            new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.TypesChanged, [], ["articlePage", "Image"], null),
            500);

        Assert.Equal(["schema-articlepage", "schema-image", "type-articlepage", "type-image"], plan.Keys);
    }

    [Fact]
    public void A_language_or_domain_change_purges_everything()
    {
        PurgePlan plan = new PurgePlanner(new Tree()).Plan(
            new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Everything, [], [], null),
            500);

        Assert.True(plan.Everything);
    }
}
