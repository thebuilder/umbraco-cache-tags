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
    internal sealed class Tree(bool typesFail = false) : IDescendants
    {
        private static readonly Dictionary<Guid, string> Types = new() { [Child] = "guidePage", [Grandchild] = "parkingPage", [Image] = "image" };

        public IEnumerable<Guid> Of(Guid key) => key == C ? [Child, Grandchild] : [];

        public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys, bool media)
            => typesFail ? throw new InvalidOperationException("The database is busy.") : keys.Select(key => Types[key]);

        // C and what is under it are in the site; the second site has its own root.
        // Trashed is in the recycle bin, so it has no root.
        public IEnumerable<(Guid Key, string Alias, Guid? Root)> TypesAndRootsOf(IReadOnlyCollection<Guid> keys)
            => keys.Select(key => (key, Types.GetValueOrDefault(key, "newsPage"), key == Trashed ? null : (Guid?)(key == OtherPage ? OtherSite : Site)));

        public IEnumerable<Guid> Roots() => [Site, OtherSite];
    }

    private static readonly Guid OtherSite = new("30000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPage = new("30000000-0000-0000-0000-000000000002");
    private static readonly Guid Trashed = new("30000000-0000-0000-0000-000000000003");

    [Fact]
    public void A_publish_reaches_its_own_roots_listings_and_names_that_root()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site, Roots: [Site]));

        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-000000000001",
                "content-10000000-0000-0000-0000-00000000000c",
                "search-deliveryapicontentindex",
                "search-deliveryapicontentindex-10000000-0000-0000-0000-000000000001",
                "type-newspage",
                "type-newspage-10000000-0000-0000-0000-000000000001",
            ],
            plan.Keys);
        Assert.Equal([Site], plan.Roots);
    }

    [Fact]
    public void A_move_to_another_root_reaches_both_roots_listings_and_its_descendants_types_in_both()
    {
        PurgePlan plan = Plan(ChangeKind.Moved, new ChangedItem(C, "newsPage", OtherSite, OldParentKey: Site, Roots: [OtherSite, Site]));

        Assert.Contains("type-newspage-10000000-0000-0000-0000-000000000001", plan.Keys);
        Assert.Contains("type-newspage-30000000-0000-0000-0000-000000000001", plan.Keys);
        Assert.Contains("type-guidepage-10000000-0000-0000-0000-000000000001", plan.Keys);
        Assert.Contains("type-guidepage-30000000-0000-0000-0000-000000000001", plan.Keys);
        Assert.Equal([Site, OtherSite], plan.Roots!.Order());
    }

    [Fact]
    public void Media_and_changes_recorded_without_roots_may_concern_any_site()
    {
        Assert.Null(Plan(ChangeKind.MediaSaved, new ChangedItem(Image, "image", Folder, Roots: [Site])).Roots);
        Assert.DoesNotContain(Plan(ChangeKind.MediaSaved, new ChangedItem(Image, "image", Folder, Roots: [Site])).Keys, key => key.EndsWith(Site.ToString("D"), StringComparison.Ordinal));
        Assert.Null(Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site)).Roots);
    }

    [Fact]
    public void A_named_document_plans_its_type_in_its_root_and_names_the_root_but_a_custom_key_concerns_every_site()
    {
        var planner = new PurgePlanner(new Tree());
        PurgePlan page = planner.Plan(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Custom, [], [], null, Keys: [$"content-{OtherPage:D}"]), 500);
        PurgePlan mixed = planner.Plan(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Custom, [], [], null, Keys: [$"content-{OtherPage:D}", "x-dictionary-da-dk"]), 500);

        Assert.Equal(["content-30000000-0000-0000-0000-000000000002", "type-newspage", "type-newspage-30000000-0000-0000-0000-000000000001"], page.Keys);
        Assert.Equal([OtherSite], page.Roots);
        Assert.Null(mixed.Roots);

        // A named document whose root is unknown may be shown by any site.
        PurgePlan unrooted = planner.Plan(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Custom, [], [], null, Keys: [$"content-{OtherPage:D}", $"content-{Trashed:D}"]), 500);
        Assert.Null(unrooted.Roots);
    }

    [Fact]
    public void A_change_with_an_item_whose_roots_could_not_be_read_may_concern_any_site()
    {
        PurgePlan plan = new PurgePlanner(new Tree()).Plan(
            new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Published, [new ChangedItem(C, "newsPage", Site, Roots: [Site]), new ChangedItem(OtherPage, "newsPage", OtherSite, Roots: [])], [], null),
            500);

        Assert.Null(plan.Roots);
    }

    [Fact]
    public void A_type_change_names_the_types_listings_in_every_root()
    {
        PurgePlan plan = new PurgePlanner(new Tree()).Plan(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.TypesChanged, [], ["articlePage"], null), 500);

        Assert.Equal(
            ["type-articlepage", "type-articlepage-10000000-0000-0000-0000-000000000001", "type-articlepage-30000000-0000-0000-0000-000000000001"],
            plan.Keys);
        Assert.Null(plan.Roots);
    }

    private static PurgePlan Plan(ChangeKind kind, ChangedItem item, int budget = 500, IReadOnlyList<string>? cultures = null, Tree? tree = null)
        => new PurgePlanner(tree ?? new Tree()).Plan(new Change(Guid.Empty, DateTime.UnixEpoch, kind, [item], [], cultures), budget);

    [Fact]
    public void A_publish_reaches_the_item_its_parents_listings_its_type_and_the_index()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site), cultures: ["da-DK"]);

        Assert.False(plan.Everything);
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
    public void A_publish_that_changes_the_route_reaches_every_descendant_and_its_type()
    {
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "newsPage", Site, RouteChanged: true));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
        Assert.Contains("content-10000000-0000-0000-0000-000000000014", plan.Keys);

        // A response past MaxItemKeys lists its items' types, not the items, so only these reach it.
        Assert.Contains("type-guidepage", plan.Keys);
        Assert.Contains("type-parkingpage", plan.Keys);
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Unpublished))]
    [InlineData(nameof(ChangeKind.Trashed))]
    [InlineData(nameof(ChangeKind.Moved))]
    public void Unpublishing_trashing_and_moving_always_reach_the_descendants_and_their_types(string kind)
    {
        PurgePlan plan = Plan(Enum.Parse<ChangeKind>(kind), new ChangedItem(C, "newsPage", Site));

        Assert.Contains("content-10000000-0000-0000-0000-000000000013", plan.Keys);
        Assert.Contains("type-guidepage", plan.Keys);
    }

    [Fact]
    public void An_unpublish_reaches_the_listings_the_descendants_leave_with_it()
    {
        // Unpublishing C takes its guide and parking page out of every listing of their types, also
        // out of pages of them that do not show them. (Publishing C again, Umbraco publishes them
        // again too, which plans their types as any publish does.)
        PurgePlan plan = Plan(ChangeKind.Unpublished, new ChangedItem(C, "category", Site, RouteChanged: true));

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
    public void Unpublishing_one_culture_reaches_the_listings_the_descendants_leave_in_it()
    {
        // Raised as a publish of the other cultures.
        PurgePlan plan = Plan(ChangeKind.Published, new ChangedItem(C, "category", Site, RouteChanged: true), cultures: ["da-DK"]);

        Assert.Contains("type-guidepage", plan.Keys);
        Assert.Contains("type-parkingpage", plan.Keys);
    }

    [Fact]
    public void A_route_change_whose_descendants_types_cannot_be_read_purges_everything()
    {
        PurgePlan plan = Plan(ChangeKind.Unpublished, new ChangedItem(C, "category", Site, RouteChanged: true), tree: new Tree(typesFail: true));

        Assert.True(plan.Everything);
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
    public void A_type_change_purges_everything_and_names_each_types_listings()
    {
        PurgePlan plan = new PurgePlanner(new Tree()).Plan(
            new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.TypesChanged, [], ["articlePage", "Image"], null),
            500);

        Assert.True(plan.Everything);
        Assert.Contains("type-articlepage", plan.Keys);
        Assert.Contains("type-image", plan.Keys);
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
