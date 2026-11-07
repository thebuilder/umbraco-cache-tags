using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Tests;

/// <summary>A purge by hand, as the backoffice's Purge view and the Management API record it.</summary>
public sealed class ManualPurgeTests : IDisposable
{
    // A set key as a playground response carried it.
    private const string SetKey = "set-ad4143dc08ca6d0c40ce5719a8e5c51b";

    private readonly TestDatabase _database = new();
    private readonly LedgerStore _ledger;
    private readonly ICacheTagsChanges _changes;

    public ManualPurgeTests()
    {
        var identity = new ServerIdentity(TimeProvider.System);
        var options = new TestOptions(new CacheTagsOptions());
        _ledger = new LedgerStore(_database.Package);
        var acks = new AckProcessor(
            _ledger,
            new KeySetStore(_database.Package),
            new OutputCacheEviction(Fake.Of<IServiceProvider>()),
            new DatabaseAckTransport(_database.Package),
            identity,
            new DeliverySignal(),
            options,
            TimeProvider.System,
            NullLogger<AckProcessor>.Instance);
        var recorder = new ChangeRecorder(new PurgePlanner(new PurgePlannerTests.Tree()), _database.Package, _ledger, identity, new DeliverySignal(), options, NullLogger<ChangeRecorder>.Instance);
        _changes = new CacheTagsChanges(recorder, acks, TimeProvider.System);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void A_set_key_copied_from_a_response_can_be_purged_by_hand()
    {
        _changes.Record([SetKey], reason: "purged by an administrator");

        Assert.Equal([SetKey], Assert.Single(_ledger.Latest(10)).Plan.Keys);
    }

    [Theory]
    [InlineData("set-AD4143DC08CA6D0C40CE5719A8E5C51B")]
    [InlineData("set-ad4143dc")]
    [InlineData("type-article")]
    public void A_key_no_response_carries_in_that_form_is_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => _changes.Record([key]));
        Assert.Empty(_ledger.Latest(10));
    }
}
