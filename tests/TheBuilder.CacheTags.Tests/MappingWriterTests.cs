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
    [Fact]
    public async Task A_render_whose_write_fails_is_written_again_without_another_request()
    {
        var store = new FlakyStore(failures: 1);
        using MappingWriter writer = Writers.Create(store);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(writer.Record(Writers.Response("response-1")));

        Assert.Equal(["response-1"], await store.Recorded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_render_that_does_not_fit_the_queue_is_refused()
    {
        // Not started, so nothing takes writes off the queue.
        using MappingWriter writer = Writers.Create(new FlakyStore(failures: 0));
        for (var i = 0; i < MappingWriter.Capacity; i++)
        {
            Assert.True(writer.Record(Writers.Response($"response-{i}")));
        }

        Assert.False(writer.Record(Writers.Response("response-one-too-many")));
    }

    [Fact]
    public void A_render_before_the_package_is_installed_is_refused()
    {
        using MappingWriter writer = Writers.Create(new FlakyStore(failures: 0), installed: false);

        Assert.False(writer.Record(Writers.Response("response-1")));
    }

    private sealed class FlakyStore(int failures) : ICacheTagStore
    {
        private int _failures = failures;

        public TaskCompletionSource<string[]> Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                throw new InvalidOperationException("The database is busy.");
            }

            Recorded.TrySetResult(responses.Select(response => response.ResponseKey).ToArray());
            return Task.CompletedTask;
        }

        public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default) => Task.FromResult<CachedResponse?>(null);

        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}

internal static class Writers
{
    public static MappingWriter Create(ICacheTagStore store, bool installed = true, CacheTagsOptions? options = null)
        => new(store, Database(installed), Options(options ?? new CacheTagsOptions()), TimeProvider.System, NullLogger<MappingWriter>.Instance);

    public static IOptionsMonitor<CacheTagsOptions> Options(CacheTagsOptions options)
        => Fake<IOptionsMonitor<CacheTagsOptions>>.Of((method, _) => method.Name == "get_CurrentValue" ? options : null);

    /// <summary>A package database whose migrations have run, or not; it is never read or written.</summary>
    public static PackageDatabase Database(bool installed)
    {
        var finalState = new CacheTagsMigrationPlan().FinalState;
        return new PackageDatabase(
            Fake<IScopeProvider>.Of(),
            Fake<IRuntimeState>.Of((method, _) => method.Name == "get_Level" ? RuntimeLevel.Run : null),
            Fake<IKeyValueService>.Of((method, _) => method.Name == "GetValue" && installed ? finalState : null));
    }

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
