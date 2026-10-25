extern alias UmbracoDelivery;

using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using DeliveryControllers = UmbracoDelivery::Umbraco.Cms.Api.Delivery.Controllers.Content;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     The result filter in a real ASP.NET pipeline: routing, authentication and MVC, with Umbraco's own
///     Delivery API request services deciding what a preview request is. The endpoint stands in for
///     Umbraco's item by id endpoint (DeliveryControllers.cs).
/// </summary>
public sealed class DeliveryPipelineTests : IAsyncLifetime
{
    private const string Item = "/umbraco/delivery/api/v2/content/item/e0000000-0000-0000-0000-000000000003";
    private static readonly CacheTagsOptions Settings = new();

    private readonly TestDatabase _database = new();
    private IHost? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    // Umbraco's own services for what a request asks for (internal, so by name).
                    services.AddHttpContextAccessor();
                    services.AddScoped(typeof(IRequestPreviewService), UmbracoService("RequestPreviewService"));
                    services.AddScoped(typeof(IRequestCultureService), UmbracoService("RequestCultureService"));
                    services.AddSingleton(Fake.Of<IRequestStartItemProvider>());
                    services.AddSingleton(Fake.Of<IVariationContextAccessor>());

                    // The filter as CacheTagsComposer registers it, with what it needs.
                    services.AddScoped<CacheKeyResultFilter>();
                    services.Configure<MvcOptions>(options => options.Filters.AddService<CacheKeyResultFilter>());
                    services.AddSingleton<IOptionsMonitor<CacheTagsOptions>>(new TestOptions(Settings));
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(Names.Of(["article"]));
                    services.AddScoped<CacheTagsResponse>();
                    services.AddSingleton(new MappingWriter(new NoStore(), _database.Package, new TestOptions(Settings), TimeProvider.System, NullLogger<MappingWriter>.Instance));
                    services.AddScoped(provider => new ResponseCollector(
                        null!,
                        Fake.Of<IPublishedContentCache>(),
                        Fake.Of<IPublishedMediaCache>(),
                        provider.GetRequiredService<IVariationContextAccessor>(),
                        Fake.Of<IApiContentPathResolver>(),
                        Fake.Of<IApiMediaQueryService>(),
                        provider.GetRequiredService<KnownNames>()));

                    // Members sign in with a header here; how they sign in does not matter to the filter.
                    services.AddAuthentication(MemberHeader.SchemeName).AddScheme<AuthenticationSchemeOptions, MemberHeader>(MemberHeader.SchemeName, null);
                    services.AddControllers().ConfigureApplicationPartManager(parts =>
                    {
                        // Only the stand-in endpoint below, not Umbraco's own controllers.
                        parts.ApplicationParts.Clear();
                        parts.ApplicationParts.Add(new AssemblyPart(typeof(DeliveryPipelineTests).Assembly));
                    });
                })
                .Configure(app => app
                    .UseRouting()
                    .UseAuthentication()
                    .UseEndpoints(endpoints => endpoints.MapControllers())))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _database.Dispose();
    }

    private Task<HttpResponseMessage> GetAsync(string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach ((var name, var value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return _client.SendAsync(request);
    }

    private static string? Keys(HttpResponseMessage response)
        => response.Headers.TryGetValues(Settings.HeaderName, out var values) ? string.Join(", ", values) : null;

    private static string? CacheControl(HttpResponseMessage response) => response.Headers.CacheControl?.ToString();

    private static string? CdnCacheControl(HttpResponseMessage response)
        => response.Headers.TryGetValues("CDN-Cache-Control", out var values) ? string.Join(", ", values) : null;

    [Fact]
    public async Task A_published_item_carries_its_response_key_and_umbraco()
    {
        using HttpResponseMessage response = await GetAsync(Item);

        Assert.Equal(200, (int)response.StatusCode);
        var keys = Keys(response)!.Split(", ");
        Assert.StartsWith("response-", keys[0], StringComparison.Ordinal);
        Assert.Equal(["umbraco"], keys[1..]);
        Assert.Null(CacheControl(response));
    }

    [Fact]
    public async Task A_recorded_response_tells_a_CDN_to_keep_it_no_longer_than_its_mapping_is_kept()
    {
        using HttpResponseMessage response = await GetAsync(Item);

        // 90 days, the default MappingRetention.
        Assert.Equal("max-age=7776000", CdnCacheControl(response));
    }

    [Fact]
    public async Task A_preview_request_is_never_stored_and_carries_no_keys()
    {
        using HttpResponseMessage response = await GetAsync(Item, ("Preview", "true"));

        Assert.Equal("no-store", CacheControl(response));
        Assert.Null(Keys(response));
    }

    [Fact]
    public async Task A_signed_in_member_s_response_is_private_and_carries_no_keys()
    {
        using HttpResponseMessage response = await GetAsync(Item, (MemberHeader.Name, "member-1"));

        Assert.Equal("private", CacheControl(response));
        Assert.Null(Keys(response));
    }

    [Fact]
    public async Task A_response_to_a_bearer_token_is_private_and_carries_no_keys_even_when_no_one_signed_in()
    {
        using HttpResponseMessage response = await GetAsync(Item, ("Authorization", "Bearer a-member-token"));

        Assert.Equal("private", CacheControl(response));
        Assert.Null(Keys(response));
    }

    [Fact]
    public async Task A_not_found_is_never_stored_and_carries_no_keys()
    {
        using HttpResponseMessage response = await GetAsync("/umbraco/delivery/api/v2/content/item/e0000000-0000-0000-0000-0000000000ff");

        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal("no-store", CacheControl(response));
        Assert.Null(CdnCacheControl(response));
        Assert.Null(Keys(response));
    }

    private static Type UmbracoService(string name)
        => typeof(DeliveryControllers.ByIdContentApiController).Assembly.GetType($"Umbraco.Cms.Api.Delivery.Services.{name}", throwOnError: true)!;

    /// <summary>Signs in whoever sends the header, as a member would be.</summary>
    private sealed class MemberHeader(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Member";
        public const string Name = "X-Test-Member";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.TryGetValue(Name, out var member)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, member.ToString())], SchemeName)), SchemeName))
                : AuthenticateResult.NoResult());
    }

    private sealed class NoStore : ICacheTagStore
    {
        public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default) => Task.FromResult<CachedResponse?>(null);

        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
