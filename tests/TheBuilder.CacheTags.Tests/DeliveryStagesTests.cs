using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Tests;

/// <summary>What the release and indexing stages queue for a target, against the ledger in SQLite.</summary>
public sealed class DeliveryStagesTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private const string Item = "content-e0000000-0000-0000-0000-000000000003";
    private const string Children = "children-e0000000-0000-0000-0000-000000000001";

    // Not installed, so the targets come from settings alone.
    private readonly TestDatabase _database = new(installed: false);
    private readonly ManualTime _time = new(Noon);
    private readonly LedgerStore _ledger;
    private readonly ServerIdentity _identity;
    private readonly PurgeTargets _targets;
    private readonly TestOptions _options = new(new CacheTagsOptions { Targets = { ["nextjs"] = new CacheTagsTargetOptions { Type = "test" } } });

    public DeliveryStagesTests()
    {
        _ledger = new LedgerStore(_database.Package);
        _identity = new ServerIdentity(_time);
        _targets = new PurgeTargets(_options, null!, _database.Package, [new NoTarget()], new ConfigurationBuilder().Build(), _time, NullLogger<PurgeTargets>.Instance);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_released_change_purges_every_planned_key_and_later_its_listings_again()
    {
        var change = new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Guid.NewGuid(), "article", Guid.Empty)], [], null);
        _ledger.Add(change, new PurgePlan([Item, Children, "type-article"], Everything: false), "backoffice");

        // No server acknowledges it, so it is released once the ack timeout has passed, and its
        // listings once no acknowledging server is left to wait for.
        _time.UtcNow = Noon.AddMinutes(2);
        await new Releaser(_ledger, Transport(), _identity, _targets, _options, _time, NullLogger<Releaser>.Instance).ReleaseAsync(CancellationToken.None);

        Assert.Equal([$"{Children} {Item} type-article"], _ledger.PurgesOf(change.Id).Select(purge => string.Join(" ", purge.Keys.Order(StringComparer.Ordinal))));

        await new IndexStage(_ledger, Transport(), null!, new OutputCacheEviction(Fake.Of<IServiceProvider>()), _identity, _targets, _options, _time, NullLogger<IndexStage>.Instance)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(
            [$"{Children} {Item} type-article", $"{Children} type-article"],
            _ledger.PurgesOf(change.Id).Select(purge => string.Join(" ", purge.Keys.Order(StringComparer.Ordinal))));
    }

    private DatabaseAckTransport Transport() => new(_database.Package);

    private sealed class NoTarget : IPurgeTarget
    {
        public string Type => "test";

        public Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
