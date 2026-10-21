using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Takes mapping writes off the request path: renders and touches are queued and written to the
///     <see cref="ICacheTagStore" /> in batches. A render whose keys were written recently is skipped,
///     and a response is touched at most once per <see cref="TouchInterval" />.
/// </summary>
internal sealed class MappingWriter : BackgroundService
{
    /// <summary>How long an identical render, or a touch, is not written again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private const int BatchSize = 200;

    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly MemoryCache _recent = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseGate _gate;
    private readonly IOptionsMonitor<CacheTagsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MappingWriter> _logger;

    public MappingWriter(IServiceScopeFactory scopeFactory, DatabaseGate gate, IOptionsMonitor<CacheTagsOptions> options, TimeProvider time, ILogger<MappingWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _gate = gate;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Queues a rendered response. Returns false when the queue is full and the write was dropped.</summary>
    public bool Record(CachedResponse response)
    {
        var fingerprint = "r:" + response.ResponseKey + ":" + string.Join(',', response.Keys.Order(StringComparer.Ordinal));
        if (_recent.TryGetValue(fingerprint, out _))
        {
            // Same keys as a render written within the hour: only the "seen" time could change.
            return Touch(response.ResponseKey);
        }

        _recent.Set(fingerprint, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });
        _recent.Set("t:" + response.ResponseKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });
        return Enqueue(response);
    }

    /// <summary>Queues a "served" touch for a response key, unless one was queued within the hour.</summary>
    public bool Touch(string responseKey)
    {
        if (_recent.TryGetValue("t:" + responseKey, out _))
        {
            return true;
        }

        _recent.Set("t:" + responseKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });
        return Enqueue(new TouchRequest(responseKey, _time.GetUtcNow().UtcDateTime));
    }

    private bool Enqueue(object item)
    {
        if (_queue.Writer.TryWrite(item))
        {
            return true;
        }

        _logger.LogWarning("The cache tag mapping queue is full; a write was dropped. That response is purged only through its broader keys until it is rendered again.");
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<object>(BatchSize);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();
            while (batch.Count < BatchSize && _queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                await _gate.RunAsync(() => WriteAsync(batch, stoppingToken), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Forget what was written recently, so the next render of these responses writes again.
                _recent.Compact(1.0);
                _logger.LogError(exception, "Writing {Count} cache tag mapping entries failed.", batch.Count);
            }
        }
    }

    private async Task WriteAsync(IReadOnlyList<object> batch, CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.Enabled is false)
        {
            return;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ICacheTagStore store = scope.ServiceProvider.GetRequiredService<ICacheTagStore>();

        CachedResponse[] renders = batch.OfType<CachedResponse>().ToArray();
        if (renders.Length > 0)
        {
            await store.RecordAsync(renders, cancellationToken);
        }

        foreach (IGrouping<DateTime, TouchRequest> touches in batch.OfType<TouchRequest>().GroupBy(t => t.SeenUtc.Date))
        {
            await store.TouchAsync(touches.Select(t => t.ResponseKey).ToArray(), touches.Max(t => t.SeenUtc), cancellationToken);
        }
    }

    public override void Dispose()
    {
        _recent.Dispose();
        base.Dispose();
    }

    private sealed record TouchRequest(string ResponseKey, DateTime SeenUtc);
}
