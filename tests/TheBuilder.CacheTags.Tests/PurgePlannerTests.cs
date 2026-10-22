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

    // C has a guide under it, and a parking page under that.
    internal sealed class Tree : IDescendants
    {
        private static readonly Dictionary<Guid, string> Types = new() { [Child] = "guidePage", [Grandchild] = "parkingPage" };

        public IEnumerable<Guid> Of(Guid key) => key == C ? [Child, Grandchild] : [];

        public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys) => keys.Select(key => Types[key]);
    }

    private static PurgePlan Plan(ChangeKind kind, ChangedItem item, int budget = 500, IReadOnlyList<string>? cultures = null)
        => new PurgePlanner(new Tree()).Plan(new Change(Guid.Empty, DateTime.UnixEpoch, kind, [item], [], cultures), budget);

    [Fact]
    public void A_publish_reaches_the_item_its_parents_listings_its_type_and_the_index()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site), cultures: ["da-DK"]);

        Assert.False(plan.Everything);
        Assert.Equal(["da-DK"], plan.Cultures);
        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-000000000001",
                "content-10000000-0000-0000-0000-00000000000c",
                "search-deliveryapicontentindex",
                "type-newspage",
            ],
            plan.Keys);
    }

    [Fact]
    public void A_publish_that_changes_the_route_reaches_every_descendant()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site, RouteChanged: true));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
        Assert.Contains("content-10000000-0000-0000-0000-000000000014", plan.Keys);
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Unpublished))]
    [InlineData(nameof(ChangeKind.Trashed))]
    [InlineData(nameof(ChangeKind.Moved))]
    public void Unpublishing_trashing_and_moving_always_reach_the_descendants(string kind)
    {
        PurgePlan plan = Plan(Enum.Parse<ChangeKind>(kind), new ChangedItem(C, "newsPage", Site));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
    }

    [Fact]
    public void A_route_change_reaches_the_listings_the_descendants_join_or_leave_with_it()
    {
        // Unpublishing C takes its guide and parking page out of every listing of their types, also
        // out of pages of them that do not show them. (Publishing C again, Umbraco publishes them
        // again too, which plans their types as any publish does.)
        PurgePlan plan = Plan(ChangeKind.Unpublished, new ChangedItem(C, "category", Site));

        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-000000000001",
                "content-10000000-0000-0000-0000-00000000000c",
                "content-10000000-0000-0000-0000-000000000013",
                "content-10000000-0000-0000-0000-000000000014",
                "search-deliveryapicontentindex",
                "type-category",
                "type-guidepage",
                "type-parkingpage",
            ],
            plan.Keys);
    }

    [Fact]
    public void A_move_reaches_the_listings_of_both_parents()
    {
        PurgePlan plan = Plan(ChangeKind.Moved, new ChangedItem(Child, "newsPage", C, OldParentKey: Site));

        Assert.Contains("children-10000000-0000-0000-0000-00000000000c", plan.Keys);
        Assert.Contains("children-10000000-0000-0000-0000-000000000001", plan.Keys);
    }

    [Fact]
    public void Too_many_descendants_purge_everything()
    {
        PurgePlan plan = Plan(ChangeKind.Unpublished, new ChangedItem(C, "newsPage", Site), budget: 1);

        Assert.True(plan.Everything);
        Assert.Empty(plan.Keys);
    }

    [Fact]
    public void A_sort_reaches_every_listing_that_can_show_the_items_in_sort_order_but_not_the_items()
    {
        PurgePlan plan = Plan(ChangeKind.Sorted, new ChangedItem(Child, "newsPage", C));

        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-00000000000c",
                "search-deliveryapicontentindex",
                "type-newspage",
            ],
            plan.Keys);
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
