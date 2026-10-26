using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Sending the outbox, against the ledger in SQLite.</summary>
public sealed class PurgeSenderTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    // Not installed, so the targets come from settings alone.
    private readonly TestDatabase _database = new(installed: false);

    public void Dispose() => _database.Dispose();

    /// <summary>
    ///     A target whose every send takes 20 seconds, within the 30 second lease, noting at the end of
    ///     each which purges another server could send then.
    /// </summary>
    private sealed class SlowTarget(LedgerStore ledger, ManualTime time) : IPurgeTarget
    {
        public List<(Guid Sending, IReadOnlyList<Guid> DueElsewhere)> Sends { get; } = [];

        public string Type => "slow";

        public Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
        {
            time.UtcNow += TimeSpan.FromSeconds(20);
            Sends.Add((request.Id, ledger.Due(time.UtcNow, 20).Due.Select(purge => purge.Id).ToList()));
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task A_purge_is_not_due_to_another_server_while_this_one_sends_it_even_after_slow_sends_before_it_in_the_same_pass()
    {
        var time = new ManualTime(Noon);
        var ledger = new LedgerStore(_database.Package);
        var change = new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Guid.NewGuid(), "page", Guid.Empty)], [], null);
        ledger.Add(change, new PurgePlan(["content-a", "content-b"], Everything: false), "backoffice");
        ledger.Release(change.Id, ChangeStage.Released, [], Noon, [new NewPurge("slow", ["content-a"], Noon), new NewPurge("slow", ["content-b"], Noon)]);

        var target = new SlowTarget(ledger, time);
        var options = new TestOptions(new CacheTagsOptions { Targets = { ["slow"] = new CacheTagsTargetOptions { Type = "slow" } } });
        var targets = new PurgeTargets(options, null!, _database.Package, [target], new ConfigurationBuilder().Build(), time, NullLogger<PurgeTargets>.Instance);
        var sender = new PurgeSender(ledger, [target], targets, new ServerIdentity(time), options, time, NullLogger<PurgeSender>.Instance);

        await sender.SendAsync(CancellationToken.None);

        Assert.Equal(2, target.Sends.Count);
        Assert.All(target.Sends, send => Assert.DoesNotContain(send.Sending, send.DueElsewhere));
        Assert.Empty(ledger.InState(OutboxStates.Pending, 10));
    }
}
