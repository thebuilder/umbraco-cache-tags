using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Services.Changes;

namespace TheBuilder.CacheTags.Tests;

public sealed class AckMatchTests
{
    private static readonly Guid Item = new("10000000-0000-0000-0000-00000000000a");
    private static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static LedgerEntry Change(DateTime createdUtc)
    {
        var change = new Change(Guid.NewGuid(), createdUtc, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null);
        return new LedgerEntry(change, "server-1", new PurgePlan([], false, null), AckSignal.For(change), ChangeStage.Pending, null, []);
    }

    private static (DateTime, ObservedRefresh) Publish(DateTime observedUtc)
        => (observedUtc, new ObservedRefresh(AckSignal.Content, Item, TreeChangeTypes.RefreshNode, HasPublishedCultures: true));

    [Fact]
    public void A_refresh_acknowledges_a_change_made_before_it_and_not_one_made_well_after()
    {
        LedgerEntry earlier = Change(Noon);
        LedgerEntry later = Change(Noon.AddMinutes(1));
        var used = new HashSet<ObservedRefresh>();

        var completed = AckProcessor.Match([earlier, later], [Publish(Noon.AddSeconds(1))], used).ToList();

        Assert.Equal([earlier.Id], completed.Select(change => change.Id));
        Assert.Single(used);
    }

    [Fact]
    public void A_refresh_seen_moments_before_its_change_is_recorded_still_counts()
    {
        LedgerEntry change = Change(Noon.AddSeconds(1));

        Assert.Single(AckProcessor.Match([change], [Publish(Noon)], new HashSet<ObservedRefresh>()));
    }

    [Fact]
    public void A_refresh_seen_seconds_before_a_change_does_not_acknowledge_it()
    {
        LedgerEntry change = Change(Noon.AddSeconds(5));

        Assert.Empty(AckProcessor.Match([change], [Publish(Noon)], new HashSet<ObservedRefresh>()));
    }

    [Fact]
    public void One_refresh_after_two_changes_to_an_item_acknowledges_both()
    {
        LedgerEntry first = Change(Noon);
        LedgerEntry second = Change(Noon.AddSeconds(1));

        var completed = AckProcessor.Match([first, second], [Publish(Noon.AddSeconds(2))], new HashSet<ObservedRefresh>()).ToList();

        Assert.Equal([first.Id, second.Id], completed.Select(change => change.Id));
    }

    [Fact]
    public void A_refresh_between_two_changes_to_an_item_acknowledges_only_the_one_before_it()
    {
        LedgerEntry first = Change(Noon);
        LedgerEntry second = Change(Noon.AddMilliseconds(500));

        var completed = AckProcessor.Match([first, second], [Publish(Noon.AddMilliseconds(200))], new HashSet<ObservedRefresh>()).ToList();

        Assert.Equal([first.Id], completed.Select(change => change.Id));
    }
}
