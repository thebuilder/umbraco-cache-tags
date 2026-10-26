using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

/// <summary>The delivery worker and the heartbeat run for the life of the site: an error must not stop them, or the host.</summary>
public sealed class BackgroundLoopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static OptionsValidationException Invalid()
        => new(Options.DefaultName, typeof(CacheTagsOptions), ["CacheTags:MaxHeaderKeys must be above 1."]);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (condition() is false)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Records heartbeats; nothing else is used.</summary>
    private sealed class Beats : IAckTransport
    {
        private int _count;

        public int Count => _count;

        public Task HeartbeatAsync(string server, DateTime firstSeenUtc, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }

        public Task LeaveAsync(string server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<ServerPresence>> GetServersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AckAsync(string server, IReadOnlyCollection<Guid> changeIds, DateTime ackedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>>> GetAcksAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task IndexedAsync(string server, IReadOnlyCollection<Guid> changeIds, DateTime indexedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_delivery_worker_keeps_running_when_its_settings_cannot_be_read()
    {
        using var database = new TestDatabase(installed: false);
        var fail = true;
        var options = new TestOptions(() => fail ? throw Invalid() : new CacheTagsOptions());
        var signal = new DeliverySignal();
        var worker = new DeliveryWorker(database.Package, null!, null!, null!, null!, null!, signal, options, NullLogger<DeliveryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => options.Reads >= 1);
            fail = false;
            signal.Wake();
            await Until(() => options.Reads >= 2);

            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_heartbeat_beats_again_once_its_settings_can_be_read()
    {
        using var database = new TestDatabase();
        var fail = true;
        var options = new TestOptions(() => fail ? throw Invalid() : new CacheTagsOptions());
        var beats = new Beats();
        var time = new FastTime();
        var heartbeat = new Heartbeat(beats, database.Package, new ServerIdentity(time), options, time, NullLogger<Heartbeat>.Instance);

        await heartbeat.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => options.Reads >= 1);
            fail = false;
            await Until(() => beats.Count >= 1);

            Assert.False(heartbeat.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await heartbeat.StopAsync(CancellationToken.None);
        }
    }

    private static (IConfigurationRoot Configuration, IOptionsMonitor<CacheTagsOptions> Options) Settings(params (string Key, string Value)[] values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        ServiceProvider services = new ServiceCollection().AddLogging().AddCacheTagsOptions(configuration).BuildServiceProvider();
        return (configuration, services.GetRequiredService<IOptionsMonitor<CacheTagsOptions>>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("ten")]
    public void Settings_that_become_invalid_on_a_reload_keep_their_last_valid_values(string invalid)
    {
        (IConfigurationRoot configuration, IOptionsMonitor<CacheTagsOptions> options) = Settings(("CacheTags:MaxHeaderKeys", "7"));
        Assert.Equal(7, options.CurrentValue.MaxHeaderKeys);

        configuration["CacheTags:MaxHeaderKeys"] = invalid;
        configuration.Reload();
        Assert.Equal(7, options.CurrentValue.MaxHeaderKeys);

        configuration["CacheTags:MaxHeaderKeys"] = "3";
        configuration.Reload();
        Assert.Equal(3, options.CurrentValue.MaxHeaderKeys);
    }

    [Fact]
    public void A_header_limit_must_leave_room_past_umbraco()
    {
        (_, IOptionsMonitor<CacheTagsOptions> tooSmall) = Settings(("CacheTags:MaxHeaderKeys", "1"));
        (_, IOptionsMonitor<CacheTagsOptions> smallest) = Settings(("CacheTags:MaxHeaderKeys", "2"));

        Assert.Throws<OptionsValidationException>(() => tooSmall.CurrentValue);
        Assert.Equal(2, smallest.CurrentValue.MaxHeaderKeys);
    }

    [Fact]
    public void Settings_that_are_invalid_from_the_start_still_fail()
    {
        (_, IOptionsMonitor<CacheTagsOptions> options) = Settings(("CacheTags:MaxHeaderKeys", "0"));

        Assert.Throws<OptionsValidationException>(() => options.CurrentValue);
    }
}
