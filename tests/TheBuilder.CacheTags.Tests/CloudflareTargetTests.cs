using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class CloudflareTargetTests
{
    private static PurgeRequest Request(IReadOnlyList<string> expire, IReadOnlyList<string> stale) => new()
    {
        Id = Guid.Empty,
        ChangeId = Guid.Empty,
        TargetName = "cdn",
        Expire = expire,
        Stale = stale,
    };

    [Fact]
    public async Task A_purge_posts_every_key_as_a_tag_to_the_zone_with_the_token()
    {
        HttpRequestMessage message = CloudflareTarget.Requests(Request(["content-a", "content-b"], ["type-article"]), "023e105f4ecef8ad9ca31a8372d0c353", "secret-token").Single();

        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("https://api.cloudflare.com/client/v4/zones/023e105f4ecef8ad9ca31a8372d0c353/purge_cache", message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer secret-token", message.Headers.Authorization!.ToString());
        Assert.Equal("""{"tags":["content-a","content-b","type-article"]}""", await message.Content!.ReadAsStringAsync());
    }

    [Fact]
    public void A_purge_of_more_tags_than_one_call_takes_is_split_across_calls()
    {
        var keys = Enumerable.Range(0, 250).Select(i => $"x-{i}").ToList();

        Assert.Equal(3, CloudflareTarget.Requests(Request(keys, []), "zone", "token").Count());
    }

    [Theory]
    [InlineData("cloudflare", true, "z", "t", true)]
    [InlineData("cloudflare", true, "z", "", false)]
    [InlineData("cloudflare", true, "", "t", false)]
    [InlineData("cloudflare", false, "", "", true)]
    [InlineData("webhook", true, "", "", true)]
    public void A_cloudflare_target_needs_a_zone_and_a_token(string type, bool enabled, string zone, string token, bool valid)
    {
        var target = new CacheTagsTargetOptions { Type = type, Enabled = enabled };
        target.Settings[CloudflareTarget.ZoneIdSetting] = zone;
        target.Settings[CloudflareTarget.ApiTokenSetting] = token;

        Assert.Equal(valid, CloudflareTarget.IsValid(target));
    }
}
