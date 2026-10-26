using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Services.Changes;

namespace TheBuilder.CacheTags.Tests;

public sealed class AckMatchTests
{
    private static readonly Guid Item = new("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid Other = new("10000000-0000-0000-0000-00000000000b");
    private static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static LedgerEntry Change(DateTime createdUtc, Guid? item = null)
    {
        var change = new Change(Guid.NewGuid(), createdUtc, ChangeKind.Published, [new ChangedItem(item ?? Item, "page", Guid.Empty)], [], null);
        return new LedgerEntry(change, "server-1", new PurgePlan([], false), AckSignal.For(change), ChangeStage.Pending, null, []);
    }

    private static SeenRefresh Publish(DateTime observedUtc, Guid? item = null, long batch = 1)
        => new(batch, observedUtc, batch, new ObservedRefresh(AckSignal.Content, item ?? Item, TreeChangeTypes.RefreshNode, HasPublishedCultures: true));

    private static List<Guid> Completed(IEnumerable<LedgerEntry> changes, params SeenRefresh[] refreshes)
        => AckProcessor.Match(changes, refreshes, new HashSet<ObservedRefresh>(), new HashSet<long>()).Select(change => change.Id).ToList();

    [Fact]
    public void A_refresh_acknowledges_a_change_made_before_it_and_not_one_made_well_after()
    {
        LedgerEntry earlier = Change(Noon);
        LedgerEntry later = Change(Noon.AddMinutes(1));

        Assert.Equal([earlier.Id], Completed([earlier, later], Publish(Noon.AddSeconds(1))));
    }

    [Fact]
    public void A_refresh_seen_moments_before_its_change_is_recorded_still_counts()
    {
        LedgerEntry change = Change(Noon.AddSeconds(1));

        Assert.Equal([change.Id], Completed([change], Publish(Noon)));
    }

    [Fact]
    public void A_refresh_seen_seconds_before_a_change_does_not_acknowledge_it()
    {
        LedgerEntry change = Change(Noon.AddSeconds(5));

        Assert.Empty(Completed([change], Publish(Noon)));
    }

    [Fact]
    public void One_refresh_well_after_two_changes_to_an_item_acknowledges_both()
    {
        LedgerEntry first = Change(Noon);
        LedgerEntry second = Change(Noon.AddSeconds(1));

        Assert.Equal([first.Id, second.Id], Completed([first, second], Publish(Noon.AddSeconds(5))));
    }

    [Fact]
    public void A_refresh_between_two_changes_to_an_item_acknowledges_only_the_one_before_it()
    {
        LedgerEntry first = Change(Noon);
        LedgerEntry second = Change(Noon.AddMilliseconds(500));

        Assert.Equal([first.Id], Completed([first, second], Publish(Noon.AddMilliseconds(200))));
    }

    [Fact]
    public void A_refresh_seen_just_after_two_changes_acknowledges_only_one_as_a_clock_ahead_can_make_an_earlier_refresh_look_later()
    {
        LedgerEntry first = Change(Noon);
        LedgerEntry second = Change(Noon.AddMilliseconds(500));

        Assert.Equal([first.Id], Completed([first, second], Publish(Noon.AddSeconds(1))));
    }

    [Fact]
    public void A_refresh_from_a_notification_that_acknowledged_a_change_does_not_count_for_another_change_close_to_it()
    {
        LedgerEntry onItem = Change(Noon);
        LedgerEntry onOther = Change(Noon.AddSeconds(1), Other);

        // One notification refreshed both items; its refresh of the other item came before that change.
        Assert.Equal([onItem.Id], Completed([onItem, onOther], Publish(Noon.AddMilliseconds(100), batch: 7), Publish(Noon.AddMilliseconds(100), Other, batch: 7)));

        // From its own notification, it counts.
        Assert.Equal([onItem.Id, onOther.Id], Completed([onItem, onOther], Publish(Noon.AddMilliseconds(100), batch: 7), Publish(Noon.AddMilliseconds(100), Other, batch: 8)));
    }
}
