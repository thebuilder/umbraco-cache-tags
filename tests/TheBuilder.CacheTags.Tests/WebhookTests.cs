using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Tests;

public sealed class WebhookTests
{
    private const string Body = """{"version":1,"id":"01a1606c-318c-77da-8780-ba1b54b6add8","changeId":"01a1606c-0000-7000-8000-000000000001","expire":["response-83dcadcd6bbb144ef252035d66d20df5","content-e0000000-0000-0000-0000-000000000003"],"stale":["type-article"]}""";

    private static readonly PurgeRequest Request = new()
    {
        Id = new Guid("01a1606c-318c-77da-8780-ba1b54b6add8"),
        ChangeId = new Guid("01a1606c-0000-7000-8000-000000000001"),
        TargetName = "nextjs",
        Expire = ["response-83dcadcd6bbb144ef252035d66d20df5", "content-e0000000-0000-0000-0000-000000000003"],
        Stale = ["type-article"],
    };

    [Fact]
    public void The_body_is_versioned_compact_json_with_the_keys_to_expire_and_to_mark_stale()
    {
        Assert.Equal(Body, WebhookTarget.Body(Request));
    }

    // Expected value computed outside the package, with Python's hmac over the same contract the
    // Next.js sample verifies: method, path, timestamp and body, joined by newlines.
    [Fact]
    public void The_signature_is_hmac_sha256_of_method_path_timestamp_and_body()
    {
        Assert.Equal(
            "sha256=428a8792aa77427497fea5519d3585cdbf7ac9baeff72580667b9ba1f3757382",
            WebhookTarget.Sign("example-local-webhook-secret-0123456789", "POST", "/api/cache-tags", 1792612800, Body));
    }

    [Theory]
    [InlineData("content-e0000000-0000-0000-0000-000000000003", "", true)]
    [InlineData("media-e0000000-0000-0000-0000-000000000003", "", true)]
    [InlineData("shop-content-e0000000-0000-0000-0000-000000000003", "shop-", true)]
    [InlineData("type-article", "", false)]
    [InlineData("children-e0000000-0000-0000-0000-000000000003", "", false)]
    [InlineData("umbraco", "", false)]
    [InlineData("shop-umbraco", "shop-", false)]
    public void Items_and_custom_keys_expire_while_selectors_go_stale(string key, string prefix, bool expires)
    {
        Assert.Equal(expires, CacheKeys.Expires(key, prefix));
    }

    [Theory]
    [InlineData("https://example.com/api/cache-tags", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://localhost:3000/api/cache-tags", "0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://example.com/api/cache-tags", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("/api/cache-tags", "0123456789abcdef0123456789abcdef", false)]
    [InlineData("https://example.com/api/cache-tags", "0123456789abcdef0123456789abcde", false)]
    public void A_webhook_needs_https_except_on_localhost_and_a_long_secret(string url, string secret, bool valid)
    {
        Assert.Equal(valid, WebhookTarget.IsValid(new CacheTagsTargetOptions { Url = url, Secret = secret }));
    }

    [Fact]
    public void A_disabled_webhook_or_another_type_is_not_checked_as_a_webhook()
    {
        Assert.True(WebhookTarget.IsValid(new CacheTagsTargetOptions { Enabled = false }));
        Assert.True(WebhookTarget.IsValid(new CacheTagsTargetOptions { Type = "cloudflare" }));
    }
}
