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

    private readonly Channel<MappingWrite> _queue = Channel.CreateBounded<MappingWrite>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly MemoryCache _recent = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly ICacheTagStore _store;
    private readonly PackageDatabase _database;
    private readonly IOptionsMonitor<CacheTagsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MappingWriter> _logger;

    public MappingWriter(ICacheTagStore store, PackageDatabase database, IOptionsMonitor<CacheTagsOptions> options, TimeProvider time, ILogger<MappingWriter> logger)
    {
        _store = store;
        _database = database;
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

        if (Enqueue(new MappingWrite.Render(response)) is false)
        {
            return false;
        }

        Remember(fingerprint);
        Remember("t:" + response.ResponseKey);
        return true;
    }

    /// <summary>Queues a "served" touch for a response key, unless one was queued within the hour.</summary>
    public bool Touch(string responseKey)
    {
        if (_recent.TryGetValue("t:" + responseKey, out _))
        {
            return true;
        }

        if (Enqueue(new MappingWrite.Touch(responseKey, _time.GetUtcNow().UtcDateTime)) is false)
        {
            return false;
        }

        Remember("t:" + responseKey);
        return true;
    }

    // Only a write that was queued is remembered: a dropped one is tried again on the next request.
    private void Remember(string key)
        => _recent.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });

    private bool Enqueue(MappingWrite item)
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
        var batch = new List<MappingWrite>(BatchSize);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();
            while (batch.Count < BatchSize && _queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                await WriteAsync(batch, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Forget what was written recently, so the next render of these responses writes again.
                _recent.Compact(1.0);
                _logger.LogError(exception, "Writing {Count} cache tag mapping entries failed.", batch.Count);
            }
        }
    }

    private async Task WriteAsync(IReadOnlyList<MappingWrite> batch, CancellationToken cancellationToken)
    {
        // Responses served before the package is installed (first boot) are recorded on a later render.
        if (_options.CurrentValue.Enabled is false || _database.IsInstalled() is false)
        {
            _recent.Compact(1.0);
            return;
        }

        CachedResponse[] renders = batch.OfType<MappingWrite.Render>().Select(write => write.Response).ToArray();
        if (renders.Length > 0)
        {
            await _store.RecordAsync(renders, cancellationToken);
        }

        MappingWrite.Touch[] touches = batch.OfType<MappingWrite.Touch>().ToArray();
        if (touches.Length > 0)
        {
            await _store.TouchAsync(touches.Select(touch => touch.ResponseKey).ToArray(), touches.Max(touch => touch.SeenUtc), cancellationToken);
        }
    }

    public override void Dispose()
    {
        _recent.Dispose();
        base.Dispose();
    }

    private abstract record MappingWrite
    {
        public sealed record Render(CachedResponse Response) : MappingWrite;

        public sealed record Touch(string ResponseKey, DateTime SeenUtc) : MappingWrite;
    }
}
