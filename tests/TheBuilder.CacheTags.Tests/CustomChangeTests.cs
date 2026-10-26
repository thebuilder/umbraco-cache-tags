using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Changes a site records itself (ICacheTagsChanges), and responses of its own tagged actions.</summary>
public sealed class CustomChangeTests
{
    private static Change Custom(string[] keys, string? waitFor = null)
        => new(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Custom, [], [], null, "test", keys, waitFor);

    [Theory]
    [InlineData("x-dictionary-da-dk", true)]
    [InlineData("content-e0000000-0000-0000-0000-000000000003", true)]
    [InlineData("media-e1000000-0000-0000-0000-000000000001", true)]
    [InlineData("umbraco", true)]
    [InlineData("x-", false)]
    [InlineData("x-Dictionary", false)]
    [InlineData("x-dictionary_da", false)]
    [InlineData("content-E0000000-0000-0000-0000-000000000003", false)]
    [InlineData("content-not-a-guid", false)]
    [InlineData("type-article", false)]
    [InlineData("response-83dcadcd6bbb144ef252035d66d20df5", false)]
    public void A_site_may_name_custom_keys_item_keys_and_everything_but_not_the_packages_own_keys(string key, bool nameable)
    {
        Assert.Equal(nameable, CacheKeys.IsNameable(key));
    }

    [Fact]
    public void A_custom_key_is_at_most_two_hundred_characters()
    {
        Assert.True(CacheKeys.IsNameable("x-" + new string('a', 198)));
        Assert.False(CacheKeys.IsNameable("x-" + new string('a', 199)));
    }

    [Theory]
    [InlineData("dictionary", true)]
    [InlineData("app-translations.v1", true)]
    [InlineData("", false)]
    [InlineData("Dictionary", false)]
    [InlineData("dictionary items", false)]
    public void A_signal_is_lowercase_letters_digits_dots_and_hyphens(string signal, bool valid)
    {
        Assert.Equal(valid, CacheTagsChanges.IsSignal(signal));
    }

    [Fact]
    public void A_custom_change_plans_exactly_its_keys()
    {
        PurgePlan plan = new PurgePlanner(new PurgePlannerTests.Tree()).Plan(Custom(["x-b", "x-a", "x-b"]), 500);

        Assert.Equal(["x-a", "x-b"], plan.Keys);
        Assert.False(plan.Everything);
    }

    [Fact]
    public void A_custom_or_manual_purge_of_an_item_plans_its_type_too_since_a_long_listing_carries_only_that()
    {
        PurgePlan plan = new PurgePlanner(new PurgePlannerTests.Tree()).Plan(
            Custom(["content-10000000-0000-0000-0000-000000000013", "media-20000000-0000-0000-0000-000000000001"]),
            500);

        Assert.Equal(
            ["content-10000000-0000-0000-0000-000000000013", "media-20000000-0000-0000-0000-000000000001", "type-guidepage", "type-image"],
            plan.Keys);
    }

    [Fact]
    public void A_custom_purge_of_an_item_whose_type_cannot_be_read_purges_everything()
    {
        PurgePlan plan = new PurgePlanner(new PurgePlannerTests.Tree(typesFail: true)).Plan(Custom(["content-10000000-0000-0000-0000-000000000013"]), 500);

        Assert.True(plan.Everything);
    }

    [Fact]
    public void A_custom_change_naming_umbraco_purges_everything()
    {
        Assert.True(new PurgePlanner(new PurgePlannerTests.Tree()).Plan(Custom(["x-a", "umbraco"]), 500).Everything);
    }

    [Fact]
    public void A_custom_change_without_a_signal_waits_for_no_server()
    {
        Assert.False(AckSignal.For(Custom(["x-a"])).WaitsForServers);
        Assert.True(AckSignal.For(Custom(["x-a"], waitFor: "dictionary")).WaitsForServers);
    }

    [Fact]
    public void A_custom_change_is_acknowledged_by_its_own_signal_only()
    {
        AckSignal signal = AckSignal.For(Custom(["x-a"], waitFor: "dictionary"));

        Assert.True(signal.IsMetBy(new ObservedRefresh(AckSignal.CustomPrefix + "dictionary", null)));
        Assert.False(signal.IsMetBy(new ObservedRefresh(AckSignal.CustomPrefix + "translations", null)));
        Assert.False(signal.IsMetBy(new ObservedRefresh(AckSignal.Everything, null)));
    }

    [Fact]
    public void A_change_to_everything_is_still_acknowledged_by_a_refresh_of_everything()
    {
        AckSignal signal = AckSignal.For(new Change(Guid.Empty, DateTime.UnixEpoch, ChangeKind.Everything, [], [], null, "domains saved"));

        Assert.True(signal.IsMetBy(new ObservedRefresh(AckSignal.Everything, null)));
    }

    [Fact]
    public void Custom_keys_expire_rather_than_go_stale()
    {
        Assert.True(CacheKeys.Expires("shop-x-dictionary-da-dk", "shop-"));
    }
}
