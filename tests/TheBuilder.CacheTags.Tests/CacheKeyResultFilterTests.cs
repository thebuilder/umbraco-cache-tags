using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace TheBuilder.CacheTags.Tests;

public sealed class CacheKeyResultFilterTests
{
    private static readonly CacheTagsOptions Settings = new();

    [Fact]
    public async Task A_response_whose_record_is_queued_carries_its_response_key()
    {
        using MappingWriter writer = Writers.Create(new NoStore());

        var keys = await HeaderAsync(writer);

        Assert.Equal(2, keys.Length);
        Assert.StartsWith("response-", keys[0], StringComparison.Ordinal);
        Assert.Equal("umbraco", keys[1]);
    }

    [Fact]
    public async Task A_response_whose_record_does_not_fit_the_queue_carries_its_keys_itself()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        for (var i = 0; i < MappingWriter.Capacity; i++)
        {
            writer.Record(Writers.Response($"response-{i}"));
        }

        Assert.Equal(["x-dictionary-en", "umbraco"], await HeaderAsync(writer));
    }

    [Fact]
    public async Task A_response_before_the_package_is_installed_carries_its_keys_itself()
    {
        using MappingWriter writer = Writers.Create(new NoStore(), installed: false);

        Assert.Equal(["x-dictionary-en", "umbraco"], await HeaderAsync(writer));
    }

    /// <summary>Runs the filter over a <see cref="DictionaryController" /> response, and returns its header's keys.</summary>
    private static async Task<string[]> HeaderAsync(MappingWriter writer)
    {
        var named = new CacheTagsResponse();
        named.Add("x-dictionary-en");
        var filter = new CacheKeyResultFilter(
            Writers.Options(Settings),
            Fake<IRequestPreviewService>.Of(),
            Fake<IRequestCultureService>.Of(),
            Fake<IRequestSegmentService>.Of(),
            Fake<IRequestStartItemProvider>.Of(),
            Fake<IVariationContextAccessor>.Of(),
            new ResponseCollector(null!, null!, null!, null!, null!, null!, null!, null!),
            named,
            writer,
            TimeProvider.System);

        var http = new DefaultHttpContext();
        http.Request.Path = "/api/dictionary";
        var action = new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(DictionaryController).GetTypeInfo(),
            MethodInfo = typeof(DictionaryController).GetMethod(nameof(DictionaryController.Get))!,
        };
        var context = new ResultExecutingContext(
            new ActionContext(http, new RouteData(), action),
            [],
            new ObjectResult(new { tagline = "Hello" }),
            new DictionaryController());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, context.Controller)));

        return http.Response.Headers[Settings.HeaderName].ToString().Split(", ");
    }

    [CacheTagged]
    private sealed class DictionaryController : ControllerBase
    {
        public IActionResult Get() => Ok();
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
