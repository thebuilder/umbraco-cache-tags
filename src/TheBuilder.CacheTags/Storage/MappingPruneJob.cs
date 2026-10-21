using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Removes mapping rows older than <see cref="CacheTagsOptions.MappingRetention" />, once a day, on
///     one server.
/// </summary>
internal sealed class MappingPruneJob(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<MappingPruneJob> logger) : IRecurringBackgroundJob
{
    public TimeSpan Period => TimeSpan.FromDays(1);

    public TimeSpan Delay => TimeSpan.FromMinutes(5);

    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public event EventHandler PeriodChanged { add { } remove { } }

    public async Task RunJobAsync()
    {
        DateTime cutoff = time.GetUtcNow().UtcDateTime - options.CurrentValue.MappingRetention;
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<ICacheTagStore>().PruneAsync(cutoff);
        logger.LogInformation("Pruned {Count} cache tag mapping rows older than {Cutoff:u}.", removed, cutoff);
    }
}
