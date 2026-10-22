using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Api.Delivery.Controllers.Content;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     Which responses the filter tags. It runs before the result does, when the response still says
///     200 whatever the result will answer, so it has to read the status from the result.
/// </summary>
public sealed class ResultStatusTests : IAsyncLifetime
{
    private const string OldRoute = "/umbraco/delivery/api/v2/content/item/guides/getting-around/on-foot/";
    private static readonly CacheTagsOptions Settings = new();

    private readonly RecordingStore _store = new();
    private readonly MappingWriter _writer;

    public ResultStatusTests()
    {
        var database = new PackageDatabase(
            Stub<IScopeProvider>.Of(),
            Stub<IRuntimeState>.Of((method, _) => method.Name == "get_Level" ? RuntimeLevel.Run : null),
            Stub<IKeyValueService>.Of((method, _) => method.Name == nameof(IKeyValueService.GetValue) ? new CacheTagsMigrationPlan().FinalState : null));
        _writer = new MappingWriter(_store, database, Options(), TimeProvider.System, NullLogger<MappingWriter>.Instance);
    }

    public Task InitializeAsync() => _writer.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _writer.StopAsync(CancellationToken.None);
        _writer.Dispose();
    }

    [Fact]
    public async Task The_delivery_apis_redirect_from_an_old_route_gets_no_store_and_no_key_and_is_not_recorded()
    {
        // What ByRouteContentApiController answers for a route an item has moved away from.
        HttpResponse response = await RunAsync(typeof(ByRouteContentApiController), OldRoute, new RedirectResult("/guides/getting-around/on-foot-renamed/", permanent: true));

        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.False(response.Headers.ContainsKey(Settings.HeaderName));
        Assert.DoesNotContain(OldRoute, await RecordedPathsAsync());
    }

    public static TheoryData<string, IActionResult> NotOk => new()
    {
        { "a challenge", new ChallengeResult() },
        { "a forbid", new ForbidResult() },
        { "a local redirect", new LocalRedirectResult("/elsewhere") },
        { "a typed redirect", Wrap(TypedResults.Redirect("/elsewhere")) },
        { "a typed not found", Wrap(TypedResults.NotFound()) },
        { "a not found among typed results", Wrap((Results<Ok<string>, NotFound>)TypedResults.NotFound()) },
    };

    [Theory]
    [MemberData(nameof(NotOk))]
    public async Task A_tagged_action_answering_anything_but_a_200_gets_no_store_and_no_key(string what, IActionResult result)
    {
        HttpResponse response = await RunAsync(typeof(TaggedController), "/api/tagged", result);

        Assert.True(response.Headers.CacheControl == "no-store", what);
        Assert.False(response.Headers.ContainsKey(Settings.HeaderName), what);
        Assert.DoesNotContain("/api/tagged", await RecordedPathsAsync());
    }

    public static TheoryData<string, IActionResult> Ok => new()
    {
        { "an ok object", new OkObjectResult("value") },
        { "a typed ok", Wrap(TypedResults.Ok("value")) },
        { "an ok among typed results", Wrap((Results<Ok<string>, NotFound>)TypedResults.Ok("value")) },
    };

    [Theory]
    [MemberData(nameof(Ok))]
    public async Task A_tagged_action_answering_a_200_is_tagged_and_recorded(string what, IActionResult result)
    {
        HttpResponse response = await RunAsync(typeof(TaggedController), "/api/tagged", result);

        Assert.True(response.Headers[Settings.HeaderName].ToString().StartsWith("response-", StringComparison.Ordinal), what);
        Assert.Contains("/api/tagged", await RecordedPathsAsync());
    }

    /// <summary>Runs the filter over a request answered with <paramref name="result" />, and returns its response.</summary>
    private async Task<HttpResponse> RunAsync(Type controller, string path, IActionResult result)
    {
        var filter = new CacheKeyResultFilter(
            Options(),
            Stub<IRequestPreviewService>.Of(),
            Stub<IRequestCultureService>.Of(),
            Stub<IRequestSegmentService>.Of(),
            Stub<IRequestStartItemProvider>.Of(),
            Stub<IVariationContextAccessor>.Of(),
            new ResponseCollector(null!, null!, null!, null!, null!, null!),
            new CacheTagsResponse(),
            _writer,
            TimeProvider.System);

        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var action = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = typeof(TaggedController).GetMethod(nameof(TaggedController.Get))!,
        };
        var context = new ResultExecutingContext(new ActionContext(http, new RouteData(), action), [], result, new object());
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], result, context.Controller)));
        return http.Response;
    }

    /// <summary>
    ///     The paths recorded so far. Runs a 200 behind them and waits for it: the writer records in
    ///     order, so anything queued before it is recorded by then.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> RecordedPathsAsync()
    {
        await RunAsync(typeof(TaggedController), "/api/marker", new OkObjectResult("marker"));
        await _store.Marked.WaitAsync(TimeSpan.FromSeconds(10));
        return _store.Paths;
    }

    // MVC wraps an IResult an action returns in its internal HttpActionResult.
    private static IActionResult Wrap(IResult result)
        => (IActionResult)Activator.CreateInstance(
            typeof(ControllerBase).Assembly.GetType("Microsoft.AspNetCore.Mvc.HttpActionResult", throwOnError: true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [result],
            culture: null)!;

    private static IOptionsMonitor<CacheTagsOptions> Options()
        => Stub<IOptionsMonitor<CacheTagsOptions>>.Of((method, _) => method.Name is "get_CurrentValue" or "Get" ? Settings : null);

    [CacheTagged]
    private sealed class TaggedController
    {
        public static void Get()
        {
        }
    }

    private sealed class RecordingStore : ICacheTagStore
    {
        private readonly List<string> _paths = [];

        public TaskCompletionSource Marker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Marked => Marker.Task;

        public IReadOnlyCollection<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return _paths.ToList();
                }
            }
        }

        public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default)
        {
            lock (_paths)
            {
                _paths.AddRange(responses.Select(response => response.Path));
            }

            if (responses.Any(response => response.Path == "/api/marker"))
            {
                Marker.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
