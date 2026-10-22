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
///     not given up while the store can be reached. When a batch fails, the renders the store did not
///     record are written again one by one, and those that fail again are retried after 2, 4, 8 ...
///     seconds (at most a minute). A render that keeps failing while others are written is one the
///     store rejects: it is given up after <see cref="MaxStrikes" /> such rounds, so it cannot hold the
///     writer in its failing state. While renders are failing, and once the writer stops,
///     <see cref="Record" /> returns false, and the response carries its keys itself instead (see
///     <c>CacheKeyResultFilter</c>).
/// </remarks>
internal sealed class MappingWriter : BackgroundService
{
    /// <summary>How long an identical render, or a touch, is not written again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>How many writes wait in the queue before more are refused.</summary>
    public const int Capacity = 10_000;

    /// <summary>How long a stopping server spends writing what is still queued or failing.</summary>
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>How many retry rounds, in which other writes went through, a render may fail before it is given up.</summary>
    public const int MaxStrikes = 3;

    private const int BatchSize = 200;

    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(1);

    // How often a refused write is logged, and a missing install checked again.
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InstallCheckInterval = TimeSpan.FromSeconds(5);

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

    // Renders the store did not record, retried at _retryAtUtc. Used by the background loop only;
    // _failingCount is its size, for Record.
    private readonly List<Failing> _failing = [];
    private int _failedRounds;
    private DateTimeOffset _retryAtUtc;
    private bool _wroteSinceRound;

    private volatile int _failingCount;
    private volatile bool _closed;
    private long _warnedTicks = long.MinValue / 2;
    private long _notInstalledUntilTicks;

    public MappingWriter(ICacheTagStore store, PackageDatabase database, IOptionsMonitor<CacheTagsOptions> options, TimeProvider time, ILogger<MappingWriter> logger)
    {
        _store = store;
        _database = database;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    ///     Queues a rendered response. Returns false when a purge cannot be relied on to reach the
    ///     response through its <c>response-</c> key: the render could not be queued (the queue is full,
    ///     the writer has stopped, or the package's tables do not exist yet), or the store is failing.
    /// </summary>
    public bool Record(CachedResponse response)
    {
        if (_closed || Installed() is false)
        {
            return false;
        }

        // While renders are failing, one the store has not recorded yet would be missed by a purge
        // released meanwhile, so the response carries its keys itself. Its render is still queued: it
        // records the mapping, and whether it goes through shows whether the store works again.
        var reliable = _failingCount == 0;
        var fingerprint = Fingerprint(response);
        if (_recent.TryGetValue(fingerprint, out _))
        {
            // Same keys as a render queued within the hour: only the "seen" time could change.
            Touch(response.ResponseKey);
            return reliable;
        }

        if (Enqueue(new MappingWrite.Render(response)) is false)
        {
            return false;
        }

        Remember(fingerprint);
        Remember("t:" + response.ResponseKey);
        return reliable;
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

    private static string Fingerprint(CachedResponse response)
        => "r:" + response.ResponseKey + ":" + string.Join(',', response.Keys.Order(StringComparer.Ordinal));

    // Only a write that was queued is remembered: one that was not is tried again on the next request.
    private void Remember(string key)
        => _recent.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TouchInterval });

    // Before a first boot's migrations have run there is nowhere to write. Checked again every few
    // seconds until they have, rather than read from the database on every request.
    private bool Installed()
    {
        var now = _time.GetUtcNow().UtcTicks;
        if (now < Interlocked.Read(ref _notInstalledUntilTicks))
        {
            return false;
        }

        if (_database.IsInstalled())
        {
            return true;
        }

        Interlocked.Exchange(ref _notInstalledUntilTicks, now + InstallCheckInterval.Ticks);
        return false;
    }

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
            while (await WaitForWritesAsync(stoppingToken))
            {
                if (_failing.Count > 0 && _time.GetUtcNow() >= _retryAtUtc)
                {
                    await WriteFailingAsync(stoppingToken);
                }

                Fill(batch, BatchSize);
                await WriteBatchAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested is false)
        {
            _logger.LogError(exception, "The cache tag mapping writer stopped unexpectedly. Responses carry their keys themselves from now on.");
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: what is left is written below.
        }
        finally
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
    // A failed batch's touches are not written again: a dropped touch leaves the "seen" time up to an
    // hour older, which only matters to pruning, days later.
    private async Task WriteBatchAsync(List<MappingWrite> batch, CancellationToken stoppingToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        List<Failing> renders = batch.OfType<MappingWrite.Render>().Select(write => new Failing(write.Response)).ToList();
        try
        {
            await WriteAsync(batch, stoppingToken);
            _wroteSinceRound = true;
        }
        catch (PartialRecordException partial) when (stoppingToken.IsCancellationRequested is false)
        {
            // The store tried each response already: the ones it did not record wait for the next round.
            _logger.LogWarning(partial, "Recording {Count} of {Total} cache tag responses failed; trying them again.", partial.ResponseKeys.Count, renders.Count);
            var notRecorded = partial.ResponseKeys.ToHashSet(StringComparer.Ordinal);
            AddFailing(renders.Where(render => notRecorded.Contains(render.Response.ResponseKey)));
            _wroteSinceRound |= notRecorded.Count < renders.Count;
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested is false)
        {
            _logger.LogWarning(exception, "Writing {Count} cache tag mapping entries failed; writing the responses one by one.", batch.Count);
            (List<Failing> notWritten, var wrote) = await WriteOneByOneAsync(renders, stoppingToken);
            AddFailing(notWritten);
            _wroteSinceRound |= wrote;
        }
    }

    // Writes renders one by one until two in a row fail: the store is then taken to be down, and the
    // rest are not tried. Returns those not written, untried ones first, and whether any was written.
    private async Task<(List<Failing> NotWritten, bool Wrote)> WriteOneByOneAsync(IReadOnlyList<Failing> renders, CancellationToken stoppingToken)
    {
        var untried = new List<Failing>();
        var rejected = new List<Failing>();
        var wrote = false;
        var inARow = 0;
        foreach (Failing render in renders)
        {
            if (inARow >= 2)
            {
                untried.Add(render);
                continue;
            }

            try
            {
                await WriteAsync([new MappingWrite.Render(render.Response)], stoppingToken);
                wrote = true;
                inARow = 0;
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested is false)
            {
                _logger.LogDebug(exception, "Writing cache tag response {ResponseKey} failed.", render.Response.ResponseKey);
                rejected.Add(render);
                inARow++;
            }
        }

        return ([.. untried, .. rejected], wrote);
    }

    // New failures wait for the round already scheduled, if there is one, rather than retrying the
    // older failing renders before their delay.
    private void AddFailing(IEnumerable<Failing> renders)
    {
        var wasEmpty = _failing.Count == 0;
        _failing.AddRange(renders);
        if (wasEmpty && _failing.Count > 0)
        {
            _failedRounds = 1;
            _retryAtUtc = _time.GetUtcNow() + Backoff.After(_failedRounds, LongestRetry);
        }

        _failingCount = _failing.Count;
    }

    // One retry round. _failing is only replaced once the round is done, so a round cut short by
    // stopping loses nothing.
    private async Task WriteFailingAsync(CancellationToken stoppingToken)
    {
        (List<Failing> notWritten, var wrote) = await WriteOneByOneAsync([.. _failing], stoppingToken);
        var othersWrote = wrote || _wroteSinceRound;
        _wroteSinceRound = false;

        _failing.Clear();
        foreach (Failing render in notWritten)
        {
            // Failing while other writes go through: the store rejects this one. It is given up after a
            // few rounds, so it cannot hold the writer in its failing state; a purge then reaches its
            // response only through "umbraco".
            if (othersWrote && ++render.Strikes >= MaxStrikes)
            {
                _recent.Remove(Fingerprint(render.Response));
                _logger.LogError("Cache tag response {ResponseKey} could not be recorded and is given up; a purge reaches it only through \"umbraco\".", render.Response.ResponseKey);
                continue;
            }

            _failing.Add(render);
        }

        _failingCount = _failing.Count;
        if (_failing.Count == 0)
        {
            _failedRounds = 0;
            return;
        }

        TimeSpan delay = Backoff.After(++_failedRounds, LongestRetry);
        _retryAtUtc = _time.GetUtcNow() + delay;
        _logger.LogError("Writing {Count} cache tag responses failed; trying again in {Delay}.", _failing.Count, delay);
    }

    // Stopping (or stopped unexpectedly): refuse new writes, so responses from now on carry their keys
    // themselves, and spend up to ShutdownBudget writing what is still queued or failing, one by one
    // where a batch fails. A crash, or what is left after the budget, loses those records.
    private async Task DrainAsync(List<MappingWrite> batch)
    {
        _closed = true;
        _failingCount = 0;
        _queue.Writer.TryComplete();
        Fill(batch, int.MaxValue);
        batch.AddRange(_failing.Select(render => new MappingWrite.Render(render.Response)));
        DateTimeOffset deadline = _time.GetUtcNow() + ShutdownBudget;
        var notWritten = 0;
        foreach (MappingWrite[] chunk in batch.Chunk(BatchSize))
        {
            MappingWrite.Render[] renders = chunk.OfType<MappingWrite.Render>().ToArray();
            if (_time.GetUtcNow() > deadline)
            {
                notWritten += renders.Length;
                continue;
            }

            try
            {
                await WriteAsync(chunk, CancellationToken.None);
                continue;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Writing {Count} cache tag mapping entries on shutdown failed; writing the responses one by one.", chunk.Length);
            }

            foreach (MappingWrite.Render render in renders)
            {
                if (_time.GetUtcNow() > deadline)
                {
                    notWritten++;
                    continue;
                }

                try
                {
                    await WriteAsync([render], CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Writing cache tag response {ResponseKey} on shutdown failed.", render.Response.ResponseKey);
                    notWritten++;
                }
            }
        }

        if (notWritten > 0)
        {
            _logger.LogWarning("{Count} cache tag responses were not recorded before shutdown; a purge reaches them only through \"umbraco\".", notWritten);
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

    /// <summary>A render the store did not record, and in how many rounds it failed while others were written.</summary>
    private sealed class Failing(CachedResponse response)
    {
        public CachedResponse Response { get; } = response;

        public int Strikes { get; set; }
    }
}
