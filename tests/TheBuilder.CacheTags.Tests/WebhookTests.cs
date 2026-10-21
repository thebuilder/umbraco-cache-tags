using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class WebhookTests
{
    private static readonly Guid Id = new("01a1606c-318c-77da-8780-ba1b54b6add8");

    [Fact]
    public void The_body_is_the_id_and_keys_as_compact_json()
    {
        Assert.Equal(
            """{"id":"01a1606c-318c-77da-8780-ba1b54b6add8","keys":["response-83dcadcd6bbb144ef252035d66d20df5","content-e0000000-0000-0000-0000-000000000003"]}""",
            Webhook.Body(Id, ["response-83dcadcd6bbb144ef252035d66d20df5", "content-e0000000-0000-0000-0000-000000000003"]));
    }

    // Expected value computed outside the package, with Python's hmac over the same contract the
    // Next.js sample verifies: method, path, timestamp and body, joined by newlines.
    [Fact]
    public void The_signature_is_hmac_sha256_of_method_path_timestamp_and_body()
    {
        var body = Webhook.Body(Id, ["response-83dcadcd6bbb144ef252035d66d20df5", "content-e0000000-0000-0000-0000-000000000003"]);

        Assert.Equal(
            "sha256=40fa3015304800eddfb3ff1a3b2c394f5b0f517964cf0b5a649b53a35271c3e2",
            Webhook.Sign("example-local-webhook-secret", "POST", "/api/cache-tags", 1792612800, body));
    }

    [Fact]
    public void Keys_are_split_over_requests_in_order()
    {
        Assert.Equal([["a", "b"], ["c"]], Webhook.Chunk(["a", "b", "c"], 2));
    }
}
