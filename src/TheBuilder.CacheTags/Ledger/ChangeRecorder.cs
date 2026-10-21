using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Plans each change, resolves it to the responses it purges, and records both in the ledger.
///     Milestone 3 sends the result; until then the ledger and the log are where a plan can be checked.
/// </summary>
internal sealed class ChangeRecorder(
    PurgePlanner planner,
    IServiceScopeFactory scopeFactory,
    IScopeProvider scopeProvider,
    IOptionsMonitor<CacheTagsOptions> options,
    ILogger<ChangeRecorder> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(Change change)
    {
        CacheTagsOptions settings = options.CurrentValue;
        if (settings.Enabled is false)
        {
            return;
        }

        PurgePlan plan = planner.Plan(change, settings.DescendantBudget);
        using (IScope scope = scopeProvider.CreateScope())
        {
            scope.Database.Insert(new EventRow
            {
                Id = change.Id,
                CreatedUtc = change.CreatedUtc,
                Kind = change.Kind.ToString(),
                Server = Environment.MachineName,
                Change = JsonSerializer.Serialize(change, Json),
                Plan = JsonSerializer.Serialize(plan, Json),
            });
            scope.Complete();
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Cache tags change {ChangeId} ({Kind}) plans {Plan}.",
                change.Id,
                change.Kind,
                plan.Everything ? "every response" : string.Join(", ", plan.Keys));
        }
    }

    /// <summary>The response keys a plan reaches now, from the mapping store.</summary>
    public async Task<IReadOnlyCollection<string>> ResolveAsync(PurgePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Everything)
        {
            return [];
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICacheTagStore>().FindResponseKeysAsync(plan.Keys, plan.Cultures, cancellationToken);
    }
}
