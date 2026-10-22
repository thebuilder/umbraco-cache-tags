using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Tests;

public sealed class MappingWriterTests
{
    private static readonly InvalidOperationException Busy = new("The database is busy.");

    [Fact]
    public async Task A_render_whose_write_keeps_failing_is_written_again_without_another_request()
    {
        // The batch fails, then the render on its own, so it waits for the next round.
        var time = new ManualTime();
        var store = new Store((_, call) => call <= 2 ? Busy : null);
        using MappingWriter writer = Writers.Create(store, time: time);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(writer.Record(Writers.Response("response-1")));
        await store.WaitForCallsAsync(2);

        await AdvanceUntil(time, () => store.RecordedKeys.Contains("response-1"));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task While_a_render_is_failing_responses_carry_their_keys_themselves()
    {
        var store = new Store((_, _) => Busy);
        using MappingWriter writer = Writers.Create(store, time: new ManualTime());
        await writer.StartAsync(CancellationToken.None);

        Assert.True(writer.Record(Writers.Response("response-1")));
        await store.WaitForCallsAsync(2);

        // The same render again (its fingerprint is remembered), and a new one.
        await Eventually(() => writer.Record(Writers.Response("response-1")) is false);
        Assert.False(writer.Record(Writers.Response("response-2")));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_render_the_store_keeps_rejecting_while_others_are_written_is_given_up()
    {
        var time = new ManualTime();
        var store = new Store((responses, _) => responses.Any(response => response.ResponseKey == "response-rejected") ? Busy : null);
        using MappingWriter writer = Writers.Create(store, time: time);
        await writer.StartAsync(CancellationToken.None);

        writer.Record(Writers.Response("response-rejected"));
        await store.WaitForAttemptsAsync("response-rejected", 2);
        for (var round = 1; round <= MappingWriter.MaxStrikes; round++)
        {
            // Another render goes through, then the rejected one fails its round again.
            writer.Record(Writers.Response($"response-{round}"));
            await store.WaitForAsync($"response-{round}");
            await AdvanceUntil(time, () => store.Attempts("response-rejected") >= 2 + round);
        }

        await Eventually(() => writer.Record(Writers.Response("response-after")));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_render_is_not_given_up_while_the_store_is_down()
    {
        var time = new ManualTime();
        var down = true;
        var store = new Store((_, _) => Volatile.Read(ref down) ? Busy : null);
        using MappingWriter writer = Writers.Create(store, time: time);
        await writer.StartAsync(CancellationToken.None);

        writer.Record(Writers.Response("response-1"));
        await store.WaitForAttemptsAsync("response-1", 2);
        await AdvanceUntil(time, () => store.Attempts("response-1") >= 2 + MappingWriter.MaxStrikes + 1);

        Volatile.Write(ref down, false);
        await AdvanceUntil(time, () => store.RecordedKeys.Contains("response-1"));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task After_two_failures_in_a_row_the_rest_of_a_failed_batch_is_not_tried()
    {
        var store = new Store((_, _) => Busy);
        using MappingWriter writer = Writers.Create(store, time: new ManualTime());
        for (var i = 1; i <= 5; i++)
        {
            writer.Record(Writers.Response($"response-{i}"));
        }

        await writer.StartAsync(CancellationToken.None);

        // The batch, then the first two on their own.
        await store.WaitForCallsAsync(3);
        await Task.Delay(200);
        Assert.Equal(3, store.Calls);
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_the_responses_a_store_names_as_not_recorded_are_written_again()
    {
        var time = new ManualTime();
        var store = new Store((responses, call) => call == 1 ? new PartialRecordException(["response-2"], [Busy]) : null);
        using MappingWriter writer = Writers.Create(store, time: time);
        writer.Record(Writers.Response("response-1"));
        writer.Record(Writers.Response("response-2"));
        await writer.StartAsync(CancellationToken.None);

        await AdvanceUntil(time, () => store.RecordedKeys.Contains("response-2"));

        Assert.Equal(1, store.Attempts("response-1"));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_render_that_does_not_fit_the_queue_is_refused()
    {
        // Not started, so nothing takes writes off the queue.
        using MappingWriter writer = Writers.Create(new Store((_, _) => null));
        for (var i = 0; i < MappingWriter.Capacity; i++)
        {
            Assert.True(writer.Record(Writers.Response($"response-{i}")));
        }

        Assert.False(writer.Record(Writers.Response("response-one-too-many")));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_render_before_the_package_is_installed_is_refused()
    {
        using MappingWriter writer = Writers.Create(new Store((_, _) => null), installed: false);

        Assert.False(writer.Record(Writers.Response("response-1")));
    }

    [Fact]
    public async Task Stopping_writes_what_is_queued_and_refuses_more()
    {
        // The first write waits until the server stops, so the others are still queued then.
        var store = new Store(async (_, call, cancellationToken) =>
        {
            if (call == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return null;
        });
        using MappingWriter writer = Writers.Create(store);
        await writer.StartAsync(CancellationToken.None);
        writer.Record(Writers.Response("response-1"));
        await store.WaitForCallsAsync(1);
        writer.Record(Writers.Response("response-2"));
        writer.Record(Writers.Response("response-3"));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(["response-1", "response-2", "response-3"], store.RecordedKeys);
        Assert.False(writer.Record(Writers.Response("response-1")));
    }

    [Fact]
    public async Task Stopping_writes_the_responses_of_a_failing_batch_one_by_one()
    {
        var store = new Store(async (responses, call, cancellationToken) =>
        {
            if (call == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return responses.Any(response => response.ResponseKey == "response-2") ? Busy : null;
        });
        using MappingWriter writer = Writers.Create(store);
        await writer.StartAsync(CancellationToken.None);
        writer.Record(Writers.Response("response-1"));
        await store.WaitForCallsAsync(1);
        writer.Record(Writers.Response("response-2"));
        writer.Record(Writers.Response("response-3"));

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(["response-1", "response-3"], store.RecordedKeys);
    }

    [Fact]
    public async Task Stopping_writes_for_a_few_seconds_at_most()
    {
        // Each write after the first takes 3 seconds: the batch fails, the first response is written
        // on its own, and the budget is spent before the next.
        var time = new ManualTime();
        var store = new Store(async (responses, call, cancellationToken) =>
        {
            if (call == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            time.Advance(TimeSpan.FromSeconds(3));
            return responses.Count > 1 ? Busy : null;
        });
        using MappingWriter writer = Writers.Create(store, time: time);
        await writer.StartAsync(CancellationToken.None);
        writer.Record(Writers.Response("response-1"));
        await store.WaitForCallsAsync(1);
        writer.Record(Writers.Response("response-2"));
        writer.Record(Writers.Response("response-3"));

        await writer.StopAsync(CancellationToken.None);

        Assert.True(MappingWriter.ShutdownBudget < TimeSpan.FromSeconds(9));
        Assert.Equal(["response-1"], store.RecordedKeys);
    }

    [Fact]
    public async Task A_store_that_times_out_with_a_cancellation_is_tried_again()
    {
        var store = new Store((_, call) => call == 1 ? new TaskCanceledException("The store timed out.") : null);
        using MappingWriter writer = Writers.Create(store);
        await writer.StartAsync(CancellationToken.None);

        writer.Record(Writers.Response("response-1"));

        await store.WaitForAsync("response-1");
        await writer.StopAsync(CancellationToken.None);
    }

    // A retry round is scheduled only once the one before has finished, which a test cannot see: the
    // clock is moved on until the round it waits for has run. Rounds without other writes in between
    // count no strike, so extra ones change nothing.
    private static Task AdvanceUntil(ManualTime time, Func<bool> condition) => Eventually(() =>
    {
        time.Advance(TimeSpan.FromMinutes(1));
        return condition();
    });

    private static async Task Eventually(Func<bool> condition)
    {
        for (var tries = 0; condition() is false; tries++)
        {
            Assert.True(tries < 200, "The condition did not hold within 10 seconds.");
            await Task.Delay(50);
        }
    }

    /// <summary>
    ///     A store whose calls (counted from 1) fail when <c>behaviour</c> returns an exception, and
    ///     record their responses otherwise.
    /// </summary>
    private sealed class Store(Func<IReadOnlyCollection<CachedResponse>, int, CancellationToken, Task<Exception?>> behaviour) : ICacheTagStore
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _recorded = new();
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        private readonly ConcurrentQueue<string> _recordedKeys = new();
        private int _calls;

        public Store(Func<IReadOnlyCollection<CachedResponse>, int, Exception?> fail)
            : this((responses, call, _) => Task.FromResult(fail(responses, call)))
        {
        }

        public int Calls => Volatile.Read(ref _calls);

        public string[] RecordedKeys => _recordedKeys.Order(StringComparer.Ordinal).ToArray();

        public Task WaitForAsync(string responseKey) => Recorded(responseKey).Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task WaitForCallsAsync(int calls) => Eventually(() => Calls >= calls);

        public Task WaitForAttemptsAsync(string responseKey, int attempts) => Eventually(() => Attempts(responseKey) >= attempts);

        public int Attempts(string responseKey) => _attempts.GetValueOrDefault(responseKey);

        public async Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default)
        {
            foreach (CachedResponse response in responses)
            {
                _attempts.AddOrUpdate(response.ResponseKey, 1, (_, count) => count + 1);
            }

            var call = Interlocked.Increment(ref _calls);
            if (await behaviour(responses, call, cancellationToken) is { } exception)
            {
                throw exception;
            }

            foreach (CachedResponse response in responses)
            {
                _recordedKeys.Enqueue(response.ResponseKey);
                Recorded(response.ResponseKey).TrySetResult();
            }
        }

        private TaskCompletionSource Recorded(string responseKey)
            => _recorded.GetOrAdd(responseKey, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default) => Task.FromResult<CachedResponse?>(null);

        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}

internal static class Writers
{
    // The writer only asks whether the package is installed, so one database of each kind serves every test.
    private static readonly TestDatabase Installed = new(), NotInstalled = new(installed: false);

    public static MappingWriter Create(ICacheTagStore store, bool installed = true, CacheTagsOptions? options = null, TimeProvider? time = null)
        => new(store, (installed ? Installed : NotInstalled).Package, new TestOptions(options ?? new CacheTagsOptions()), time ?? TimeProvider.System, NullLogger<MappingWriter>.Instance);

    public static CachedResponse Response(string responseKey) => new()
    {
        ResponseKey = responseKey,
        Path = "/umbraco/delivery/api/v2/content",
        Query = string.Empty,
        Culture = string.Empty,
        Segment = string.Empty,
        StartItem = string.Empty,
        Keys = ["content-e0000000-0000-0000-0000-000000000001"],
        RenderedUtc = DateTime.UtcNow,
        SeenUtc = DateTime.UtcNow,
    };
}
