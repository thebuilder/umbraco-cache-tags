using System.Text.Json;
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
        return new LedgerEntry(change.Id, createdUtc, "Published", "server-1", default(JsonElement), new PurgePlan([], false, null), AckSignal.For(change), "Pending", null, []);
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

        Assert.Equal([(earlier.Id, Noon.AddSeconds(1))], completed.Select(item => (item.Change.Id, item.AppliedUtc)));
        Assert.Single(used);
    }

    [Fact]
    public void A_refresh_seen_moments_before_its_change_is_recorded_still_counts()
    {
        LedgerEntry change = Change(Noon.AddSeconds(2));

        Assert.Single(AckProcessor.Match([change], [Publish(Noon)], new HashSet<ObservedRefresh>()));
    }
}
