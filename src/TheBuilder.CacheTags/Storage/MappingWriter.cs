using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Takes mapping writes off the request path: renders and touches are queued and written to the
///     <see cref="ICacheTagStore" /> in batches. A render whose keys were written recently is skipped,
///     and a response is touched at most once per <see cref="TouchInterval" />.
/// </summary>
/// <remarks>
///     A response's <c>response-</c> key goes out before its render is written, so a queued render is
///     not given up while the server runs. When a batch fails, its renders are written one by one, and
///     those that fail again are retried after 2, 4, 8 ... seconds (at most a minute), so one render the
///     store keeps rejecting does not hold up the rest. Once <see cref="MaxFailing" /> renders are
///     failing (the store is down), no new writes are taken until they go through, so the queue fills.
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

    /// <summary>How many renders may be failing before the writer stops taking new writes.</summary>
    private const int MaxFailing = BatchSize;

    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(1);

    // How long a stopping server spends writing what is still queued.
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

    // How often a refused write is logged.
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

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

    // Renders the store rejected, retried at _retryAtUtc. Read and written by the background loop only.
    private readonly List<CachedResponse> _failing = [];
    private int _failedRounds;
    private DateTimeOffset _retryAtUtc;

    private long _warnedTicks;

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

        var now = _time.GetUtcNow().UtcTicks;
        var warned = Interlocked.Read(ref _warnedTicks);
        if (now - warned >= WarningInterval.Ticks && Interlocked.CompareExchange(ref _warnedTicks, now, warned) == warned)
        {
            _logger.LogWarning("The cache tag mapping queue is full or stopped, so writes are refused. A response that cannot be recorded carries its item and selector keys instead of its response key.");
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<MappingWrite>(BatchSize);
        try
        {
            while (true)
            {
                if (_failing.Count >= MaxFailing)
                {
                    // The store keeps failing: take no new writes until these go through.
                    await Task.Delay(Until(_retryAtUtc), _time, stoppingToken);
                }
                else if (await WaitForWritesAsync(stoppingToken) is false)
                {
                    return;
                }

                if (_failing.Count > 0 && _time.GetUtcNow() >= _retryAtUtc)
                {
                    await WriteFailingAsync(stoppingToken);
                }

                if (_failing.Count < MaxFailing)
                {
                    Fill(batch, BatchSize);
                    await WriteBatchAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await DrainAsync(batch);
        }
    }

    // Until writes are queued, or the failing renders are due again. False once the queue is completed.
    private async Task<bool> WaitForWritesAsync(CancellationToken stoppingToken)
    {
        if (_failing.Count == 0)
        {
            return await _queue.Reader.WaitToReadAsync(stoppingToken);
        }

        using var due = new CancellationTokenSource(Until(_retryAtUtc), _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, due.Token);
        try
        {
            return await _queue.Reader.WaitToReadAsync(either.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested is false)
        {
            return true;
        }
    }

    private TimeSpan Until(DateTimeOffset utc) => utc - _time.GetUtcNow() is { Ticks: > 0 } wait ? wait : TimeSpan.Zero;

    private void Fill(List<MappingWrite> batch, int size)
    {
        while (batch.Count < size && _queue.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }
    }

    // Any exception other than the server stopping is a failed write, a store's own timeout included.
    private async Task WriteBatchAsync(List<MappingWrite> batch, CancellationToken stoppingToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await WriteAsync(batch, stoppingToken);
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested is false)
        {
            // Touches are dropped: one only delays pruning, and the next is an hour away at most.
            _logger.LogWarning(exception, "Writing {Count} cache tag mapping entries failed; writing the responses one by one.", batch.Count);
            _failing.AddRange(batch.OfType<MappingWrite.Render>().Select(write => write.Response));
            await WriteFailingAsync(stoppingToken);
        }
    }

    // Writes the failing renders one by one. After two failures in a row the store is taken to be down
    // and the rest wait for the next round, untried ones first, so renders the store keeps rejecting do
    // not stop the others from being tried.
    private async Task WriteFailingAsync(CancellationToken stoppingToken)
    {
        CachedResponse[] pending = [.. _failing];
        _failing.Clear();
        var rejected = new List<CachedResponse>();
        var inARow = 0;
        foreach (CachedResponse response in pending)
        {
            if (inARow >= 2)
            {
                _failing.Add(response);
                continue;
            }

            try
            {
                await WriteAsync([new MappingWrite.Render(response)], stoppingToken);
                inARow = 0;
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested is false)
            {
                _logger.LogDebug(exception, "Writing cache tag response {ResponseKey} failed.", response.ResponseKey);
                rejected.Add(response);
                inARow++;
            }
        }

        _failing.AddRange(rejected);
        if (_failing.Count == 0)
        {
            _failedRounds = 0;
            return;
        }

        TimeSpan delay = Backoff.After(++_failedRounds, LongestRetry);
        _retryAtUtc = _time.GetUtcNow() + delay;
        _logger.LogError("Writing {Count} cache tag responses failed; trying again in {Delay}.", _failing.Count, delay);
    }

    // Stopping: refuse new writes, so responses from now on carry their keys themselves, and spend a
    // few seconds writing what is still queued or failing. A database already gone loses the rest.
    private async Task DrainAsync(List<MappingWrite> batch)
    {
        _queue.Writer.TryComplete();
        batch.AddRange(_failing.Select(response => new MappingWrite.Render(response)));
        Fill(batch, int.MaxValue);
        DateTimeOffset deadline = _time.GetUtcNow() + ShutdownBudget;
        var written = 0;
        try
        {
            foreach (MappingWrite[] chunk in batch.Chunk(BatchSize))
            {
                if (_time.GetUtcNow() > deadline)
                {
                    break;
                }

                await WriteAsync(chunk, CancellationToken.None);
                written += chunk.Length;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Writing cache tag mapping entries on shutdown failed.");
        }

        if (written < batch.Count)
        {
            _logger.LogWarning("{Count} cache tag mapping entries were not written before shutdown.", batch.Count - written);
        }
    }

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
