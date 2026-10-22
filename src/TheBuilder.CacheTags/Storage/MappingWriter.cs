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
/// <remarks>
///     A response's <c>response-</c> key goes out before its render is written, so a queued render is
///     never given up: a batch that fails is written again until it is written, while the queue fills.
///     A render that cannot be queued makes <see cref="Record" /> return false, and the response then
///     carries its keys itself instead (see <c>CacheKeyResultFilter</c>).
/// </remarks>
internal sealed class MappingWriter : BackgroundService
{
    /// <summary>How long an identical render, or a touch, is not written again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>How many writes wait in the queue before more are refused.</summary>
    public const int Capacity = 10_000;

    private const int BatchSize = 200;

    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(1);

    // Wait rather than DropWrite: with DropWrite, TryWrite reports a dropped write as queued. TryWrite
    // never waits either way.
    private readonly Channel<MappingWrite> _queue = Channel.CreateBounded<MappingWrite>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly MemoryCache _recent = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly ICacheTagStore _store;
    private readonly PackageDatabase _database;
    private readonly IOptionsMonitor<CacheTagsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MappingWriter> _logger;

    // 1 from the first write the queue refuses until it has emptied, so a full queue warns once.
    private int _full;

    public MappingWriter(ICacheTagStore store, PackageDatabase database, IOptionsMonitor<CacheTagsOptions> options, TimeProvider time, ILogger<MappingWriter> logger)
    {
        _store = store;
        _database = database;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    ///     Queues a rendered response. Returns false when it could not be queued (the queue is full, the
    ///     writer is stopping, or the package's tables do not exist yet): a purge could then not reach
    ///     the response through its <c>response-</c> key.
    /// </summary>
    public bool Record(CachedResponse response)
    {
        if (_database.IsInstalled() is false)
        {
            // On a first boot, before the package's migrations have run, there is nowhere to write it.
            return false;
        }

        var fingerprint = "r:" + response.ResponseKey + ":" + string.Join(',', response.Keys.Order(StringComparer.Ordinal));
        if (_recent.TryGetValue(fingerprint, out _))
        {
            // Same keys as a render queued within the hour, which is written or still being retried:
            // only the "seen" time could change, and the response key is reachable either way.
            Touch(response.ResponseKey);
            return true;
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

    // Only a write that was queued is remembered: one that was not is tried again on the next request.
    private void Remember(string key)
        => _recent.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });

    private bool Enqueue(MappingWrite item)
    {
        if (_queue.Writer.TryWrite(item))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _full, 1) == 0)
        {
            _logger.LogWarning("The cache tag mapping queue is full or stopped; writes are not queued until it has emptied. A response that cannot be recorded carries its item and selector keys instead of its response key.");
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<MappingWrite>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                Fill(batch, BatchSize);
                await WriteUntilWrittenAsync(batch, stoppingToken);
                batch.Clear();
                if (_queue.Reader.Count == 0)
                {
                    Interlocked.Exchange(ref _full, 0);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: refuse new writes, so responses from now on carry their keys themselves, and
            // write what is queued (and any batch still being retried) once more.
            _queue.Writer.TryComplete();
            Fill(batch, int.MaxValue);
            try
            {
                await WriteAsync(batch, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Writing {Count} cache tag mapping entries on shutdown failed.", batch.Count);
            }
        }
    }

    private void Fill(List<MappingWrite> batch, int size)
    {
        while (batch.Count < size && _queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }
    }

    // The responses in the batch are out with their keys already, so a failed batch is written again
    // until it goes through; writes are additive, so rewriting the part that did is harmless.
    private async Task WriteUntilWrittenAsync(IReadOnlyList<MappingWrite> batch, CancellationToken stoppingToken)
    {
        for (var failures = 0; ; failures++)
        {
            try
            {
                await WriteAsync(batch, stoppingToken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                TimeSpan delay = RetryDelay(failures);
                _logger.LogError(exception, "Writing {Count} cache tag mapping entries failed; trying again in {Delay}.", batch.Count, delay);
                await Task.Delay(delay, _time, stoppingToken);
            }
        }
    }

    /// <summary>1, 2, 4 ... seconds after a batch has failed this many times before, at most a minute.</summary>
    private static TimeSpan RetryDelay(int failures)
        => TimeSpan.FromSeconds(Math.Min(LongestRetry.TotalSeconds, Math.Pow(2, Math.Min(failures, 10))));

    private async Task WriteAsync(IReadOnlyList<MappingWrite> batch, CancellationToken cancellationToken)
    {
        // Only a change to the setting while writes were queued gets here with the package off; with
        // it off, nothing is purged either.
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
