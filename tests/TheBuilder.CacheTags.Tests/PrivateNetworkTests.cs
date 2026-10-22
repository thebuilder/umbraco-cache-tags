using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Composition;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Targets;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     The purge clients as the package's composer registers them, against a real socket on this
///     machine: the private network guard for backoffice targets, and no redirects or proxies.
///     Not run in parallel with other tests, because the proxy test sets the process-wide default proxy.
/// </summary>
[Collection(nameof(PrivateNetworkTests))]
public sealed class PrivateNetworkTests : IAsyncLifetime
{
    private const string Secret = "a-signing-secret-of-thirty-two-c";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ServiceProvider _services;
    private int _connections;
    private Task? _accepting;

    // Answered by every connection the listener accepts.
    private string _response = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public PrivateNetworkTests()
    {
        // Everything Compose registers, on a builder that scans no assemblies.
        var services = new ServiceCollection();
        var typeLoader = new TypeLoader(new TypeFinder(NullLogger<TypeFinder>.Instance, new NoAssemblies(), null, null), NullLogger<TypeLoader>.Instance, []);
        new CacheTagsComposer().Compose(new UmbracoBuilder(services, new ConfigurationBuilder().Build(), typeLoader));
        _services = services.BuildServiceProvider();
    }

    private sealed class NoAssemblies : IAssemblyProvider
    {
        public IEnumerable<Assembly> Assemblies => [];
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task InitializeAsync()
    {
        _listener.Start();
        _accepting = AcceptAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Stop();
        await (_accepting ?? Task.CompletedTask);
        await _services.DisposeAsync();
    }

    // Answers every request with _response, and counts the connections that got this far.
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _connections);
                NetworkStream stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var read = 0;
                var last = -1;
                while (last != 0 && Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n") is false)
                {
                    last = await stream.ReadAsync(buffer.AsMemory(read));
                    read += last;
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_response));
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
        {
            // Stopped.
        }
    }

    private static TargetDefinition Backoffice(string url) => new()
    {
        Name = "frontend",
        Type = WebhookTarget.TypeName,
        Url = url,
        Secret = new TargetValue(Secret),
    };

    private static ResolvedTarget Resolve(TargetDefinition target, bool allowPrivateNetwork)
        => TargetResolver.Resolve(target, [WebhookTarget.TypeName], new ConfigurationBuilder().Build(), allowPrivateNetwork);

    private Task<string?> SendAsync(CacheTagsTargetOptions target)
        => new WebhookTarget(_services.GetRequiredService<IHttpClientFactory>(), TimeProvider.System).SendAsync(
            new PurgeRequest { Id = Guid.NewGuid(), ChangeId = Guid.NewGuid(), TargetName = "frontend", Expire = ["c-e0000000-0000-0000-0000-000000000003"], Stale = [] },
            target,
            CancellationToken.None);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_backoffice_target_on_a_private_address_is_refused_before_it_connects(string host)
    {
        ResolvedTarget target = Resolve(Backoffice($"http://{host}:{Port}/api/cache-tags"), allowPrivateNetwork: false);

        var error = await SendAsync(target.Options);

        Assert.Contains("private or reserved network address", error);
        Assert.Equal(0, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task A_backoffice_target_on_a_private_address_is_sent_to_when_private_targets_are_allowed()
    {
        ResolvedTarget target = Resolve(Backoffice($"http://127.0.0.1:{Port}/api/cache-tags"), allowPrivateNetwork: true);

        Assert.Null(await SendAsync(target.Options));
        Assert.Equal(1, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_signed_purge_stays_with_the_target()
    {
        _response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{Port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var target = new CacheTagsTargetOptions { Type = WebhookTarget.TypeName, Url = $"http://127.0.0.1:{Port}/api/cache-tags", Secret = Secret };

        Assert.StartsWith("302", await SendAsync(target));
        Assert.Equal(1, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task A_backoffice_target_is_connected_to_itself_and_never_through_a_proxy()
    {
        // Through a proxy, the guard would check the proxy's address rather than the target's.
        IWebProxy before = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{Port}");
        try
        {
            ResolvedTarget target = Resolve(Backoffice("http://cache-tags-test.invalid/api/cache-tags"), allowPrivateNetwork: false);

            var error = await SendAsync(target.Options);

            Assert.Contains("cache-tags-test.invalid", error);
            Assert.DoesNotContain("127.0.0.1", error);
        }
        finally
        {
            HttpClient.DefaultProxy = before;
        }
    }

    [Fact]
    public async Task A_target_from_configuration_reaches_a_private_address()
    {
        var target = new CacheTagsTargetOptions { Type = WebhookTarget.TypeName, Url = $"http://127.0.0.1:{Port}/api/cache-tags", Secret = Secret };

        Assert.Null(await SendAsync(target));
        Assert.Equal(1, Volatile.Read(ref _connections));
    }
}

[CollectionDefinition(nameof(PrivateNetworkTests), DisableParallelization = true)]
public sealed class PrivateNetworkTestsCollection;
