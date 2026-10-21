using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The loop every server runs: release the changes every server has applied, then send the
///     purges that are due. Leases make sure only one server handles each.
/// </summary>
internal sealed class DeliveryWorker(
    AckProcessor acks,
    IScopeProvider scopeProvider,
    IServiceScopeFactory scopeFactory,
    IAckTransport transport,
    IHttpClientFactory httpClientFactory,
    ServerIdentity identity,
    DatabaseGate gate,
    DeliverySignal signal,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    public const string HttpClientName = "TheBuilder.CacheTags";

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>How often an idle server checks for work it was not told about (another server's purges, say).</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            CacheTagsOptions settings = options.CurrentValue;
            var busy = false;
            if (settings.Enabled)
            {
                busy = await gate.RunAsync(async () =>
                {
                    await Step("acknowledge refreshes", acks.ProcessAsync);
                    var unreleased = await Step("release changes", () => ReleaseAsync(settings, stoppingToken));
                    var pending = await Step("send purges", () => SendAsync(settings, stoppingToken));
                    return acks.HasQueued || unreleased > 0 || pending > 0;
                }, stoppingToken);
            }

            // While there is work, look again soon; otherwise sleep until a change or refresh wakes us.
            await signal.WaitAsync(busy ? settings.PollInterval : IdleInterval, stoppingToken);
        }
    }

    private async Task<int> Step(string what, Func<Task<int>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Before the package's tables exist (first boot), every step fails; quietly try again.
            logger.LogDebug(exception, "Cache tags could not {What}.", what);
            return 1;
        }
    }

    private async Task Step(string what, Func<Task> step)
        => await Step(what, async () =>
        {
            await step();
            return 0;
        });

    /// <summary>Releases the changes that are ready. Returns how many are still waiting.</summary>
    internal async Task<int> ReleaseAsync(CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        List<EventRow> unreleased;
        using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
        {
            unreleased = LedgerSql.Unreleased(scope.Database, 100);
        }

        if (unreleased.Count == 0)
        {
            return 0;
        }

        IReadOnlyCollection<ServerPresence> servers = await transport.GetServersAsync(cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> acked = await transport.GetAcksAsync(unreleased.Select(e => e.Id).ToList(), cancellationToken);
        DateTime now = time.GetUtcNow().UtcDateTime;

        var waiting = 0;
        foreach (EventRow change in unreleased)
        {
            // The recording server applied the change's refresh before it recorded the change.
            var acknowledgedBy = new HashSet<string>(StringComparer.Ordinal) { change.Server };
            if (acked.TryGetValue(change.Id, out IReadOnlySet<string>? by))
            {
                acknowledgedBy.UnionWith(by);
            }

            ReleaseDecision decision = ReleaseDecision.Decide(change.CreatedUtc, now, servers, acknowledgedBy, settings.AckTimeout, settings.ServerTimeout);
            if (decision.Release)
            {
                await ReleaseOneAsync(change, decision, settings, now, cancellationToken);
            }
            else
            {
                waiting++;
            }
        }

        return waiting;
    }

    private async Task ReleaseOneAsync(EventRow change, ReleaseDecision decision, CacheTagsOptions settings, DateTime now, CancellationToken cancellationToken)
    {
        using (IScope claim = WriteScope.Create(scopeProvider))
        {
            var claimed = LedgerSql.ClaimRelease(claim.Database, change.Id, identity.Name, now, now + Lease);
            claim.Complete();
            if (claimed is false)
            {
                return;
            }
        }

        PurgePlan plan = JsonSerializer.Deserialize<PurgePlan>(change.Plan, ChangeRecorder.Json)
            ?? new PurgePlan([], Everything: true, Cultures: null);
        (IReadOnlyList<string> keys, IReadOnlyList<string> listingKeys) = await PurgeKeysAsync(plan, settings, cancellationToken);
        var purges = Purges(change.Id, keys, now, settings).ToList();
        if (listingKeys.Count > 0)
        {
            // Listings again, as the Delivery API index catches up with the change.
            foreach (TimeSpan delay in settings.ListingRepurgeDelays.Where(d => d > TimeSpan.Zero).Distinct())
            {
                purges.AddRange(Purges(change.Id, listingKeys, now + delay, settings));
            }
        }


        using (IScope release = WriteScope.Create(scopeProvider))
        {
            LedgerSql.CompleteRelease(
                release.Database,
                change.Id,
                identity.Name,
                decision.TimedOut ? EventStatuses.ReleasedAfterTimeout : EventStatuses.Released,
                now,
                decision.Missing.Count == 0 ? null : JsonSerializer.Serialize(decision.Missing, ChangeRecorder.Json),
                purges);
            release.Complete();
        }

        acks.Forget(change.Id);
        if (decision.TimedOut)
        {
            logger.LogWarning(
                "Cache tags change {ChangeId} was sent after {Timeout} without acknowledgement from {Servers}. Those servers may still serve the old content.",
                change.Id,
                settings.AckTimeout,
                string.Join(", ", decision.Missing));
        }

        logger.LogInformation("Cache tags change {ChangeId} released: {KeyCount} keys in {PurgeCount} purges.", change.Id, keys.Count, purges.Count);
    }

    private static IEnumerable<OutboxRow> Purges(Guid changeId, IReadOnlyList<string> keys, DateTime notBeforeUtc, CacheTagsOptions settings)
        => settings.Targets
            .Where(target => target.Enabled)
            .SelectMany(target => Webhook.Chunk(keys, target.MaxKeysPerRequest).Select(chunk => new OutboxRow
            {
                Id = Guid.CreateVersion7(),
                EventId = changeId,
                Target = target.Name,
                Keys = JsonSerializer.Serialize(chunk, ChangeRecorder.Json),
                State = OutboxStates.Pending,
                CreatedUtc = notBeforeUtc,
                NotBeforeUtc = notBeforeUtc,
            }));

    // All: the response keys the plan reaches now, then the planned keys themselves (which direct
    // mode and selector-tagging frontends use). Listings: the responses only the plan's selector
    // keys reach, and those keys, for the second purge. All with the configured prefix.
    private async Task<(IReadOnlyList<string> All, IReadOnlyList<string> Listings)> PurgeKeysAsync(PurgePlan plan, CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        if (plan.Everything)
        {
            return ([CacheKeys.WithPrefix(settings.KeyPrefix, CacheKeys.All)], []);
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ICacheTagStore store = scope.ServiceProvider.GetRequiredService<ICacheTagStore>();
        IReadOnlyCollection<string> responses = await store.FindResponseKeysAsync(plan.Keys, plan.Cultures, cancellationToken);
        var selectors = plan.Keys.Where(CacheKeys.IsSelector).ToList();
        IReadOnlyCollection<string> listings = selectors.Count == 0 ? [] : await store.FindResponseKeysAsync(selectors, plan.Cultures, cancellationToken);

        List<string> Prefixed(IEnumerable<string> keys) => keys
            .Distinct(StringComparer.Ordinal)
            .Select(key => CacheKeys.WithPrefix(settings.KeyPrefix, key))
            .ToList();
        return (
            Prefixed(responses.Order(StringComparer.Ordinal).Concat(plan.Keys)),
            listings.Count == 0 ? [] : Prefixed(listings.Order(StringComparer.Ordinal).Concat(selectors)));
    }

    /// <summary>Sends the purges that are due. Returns how many purges are still pending, now or later.</summary>
    internal async Task<int> SendAsync(CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        List<OutboxRow> due;
        int pending;
        using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
        {
            due = LedgerSql.Due(scope.Database, now, 20);
            pending = LedgerSql.PendingCount(scope.Database);
        }

        foreach (OutboxRow purge in due)
        {
            using (IScope claim = WriteScope.Create(scopeProvider))
            {
                var claimed = LedgerSql.ClaimSend(claim.Database, purge.Id, identity.Name, now, now + Lease);
                claim.Complete();
                if (claimed is false)
                {
                    continue;
                }
            }

            var error = await SendOneAsync(purge, settings, cancellationToken);
            using IScope done = WriteScope.Create(scopeProvider);
            DateTime finished = time.GetUtcNow().UtcDateTime;
            if (error is null)
            {
                LedgerSql.Sent(done.Database, purge.Id, identity.Name, finished);
            }
            else
            {
                LedgerSql.Failed(done.Database, purge.Id, identity.Name, purge.Attempts + 1, settings.MaxAttempts, error, finished);
                logger.LogWarning("Cache tags purge {PurgeId} to {Target} failed (attempt {Attempt} of {Max}): {Error}", purge.Id, purge.Target, purge.Attempts + 1, settings.MaxAttempts, error);
            }

            done.Complete();
        }

        return Math.Max(0, pending - due.Count);
    }

    // Evicts the purge's responses from this server's Delivery API output cache before the target
    // refetches them. The acks already did so on every server for the first purge; for the second
    // (listing) purge, which comes after the index caught up, this covers the sending server only.
    // Other servers' output caches get distributed eviction with Milestone 4.
    private async Task EvictLocallyAsync(IEnumerable<string> keys, CacheTagsOptions settings)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<Umbraco.Cms.Core.Cache.IDeliveryApiOutputCacheManager>() is { } outputCache)
        {
            var prefix = settings.KeyPrefix + CacheKeys.ResponsePrefix;
            foreach (var key in keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                await outputCache.EvictByTagAsync(key[settings.KeyPrefix.Length..]);
            }
        }
    }

    /// <summary>Posts one signed purge. Returns null on success, or what went wrong.</summary>
    private async Task<string?> SendOneAsync(OutboxRow purge, CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        CacheTagsTargetOptions? target = settings.Targets.FirstOrDefault(t => t.Name == purge.Target && t.Enabled);
        if (target is null)
        {
            return $"No enabled target named {purge.Target} is configured.";
        }

        var keys = JsonSerializer.Deserialize<string[]>(purge.Keys, ChangeRecorder.Json) ?? [];
        await EvictLocallyAsync(keys, settings);
        var body = Webhook.Body(purge.Id, keys);
        var url = new Uri(target.Url);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Webhook.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(Webhook.SignatureHeader, Webhook.Sign(target.Secret, "POST", url.AbsolutePath, timestamp, body));

        try
        {
            using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? null
                : $"{(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return exception.Message;
        }
    }
}
