using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using Umbraco.Cms.Api.Delivery.Controllers.Content;
using Umbraco.Cms.Core.DeliveryApi;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     Which responses the filter tags. It runs before the result does, when the response still says
///     200 whatever the result will answer, so it has to read the status from the result.
/// </summary>
public sealed class ResultStatusTests
{
    private const string OldRoute = "/umbraco/delivery/api/v2/content/item/guides/getting-around/on-foot/";
    private static readonly CacheTagsOptions Settings = new();

    [Fact]
    public async Task The_delivery_apis_redirect_from_an_old_route_gets_no_store_and_no_key()
    {
        // What ByRouteContentApiController answers for a route an item has moved away from.
        HttpResponse response = await RunAsync(typeof(ByRouteContentApiController), OldRoute, new RedirectResult("/guides/getting-around/on-foot-renamed/", permanent: true));

        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.False(response.Headers.ContainsKey(Settings.HeaderName));
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
    }

    public static TheoryData<string, IActionResult> Ok => new()
    {
        { "an ok object", new OkObjectResult("value") },
        { "a typed ok", Wrap(TypedResults.Ok("value")) },
        { "an ok among typed results", Wrap((Results<Ok<string>, NotFound>)TypedResults.Ok("value")) },
    };

    [Theory]
    [MemberData(nameof(Ok))]
    public async Task A_tagged_action_answering_a_200_is_tagged(string what, IActionResult result)
    {
        HttpResponse response = await RunAsync(typeof(TaggedController), "/api/tagged", result);

        Assert.True(response.Headers[Settings.HeaderName] == "umbraco", what);
    }

    /// <summary>Runs the filter over a request answered with <paramref name="result" />, and returns its response.</summary>
    private static async Task<HttpResponse> RunAsync(Type controller, string path, IActionResult result)
    {
        var filter = new CacheKeyResultFilter(
            new TestOptions(Settings),
            Fake.Of<IRequestPreviewService>(),
            new ResponseCollector(null!, null!, null!, null!, null!, null!, null!),
            new CacheTagsResponse());

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

    // MVC wraps an IResult an action returns in its internal HttpActionResult.
    private static IActionResult Wrap(IResult result)
        => (IActionResult)Activator.CreateInstance(
            typeof(ControllerBase).Assembly.GetType("Microsoft.AspNetCore.Mvc.HttpActionResult", throwOnError: true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [result],
            culture: null)!;

    [CacheTagged]
    private sealed class TaggedController
    {
        public static void Get()
        {
        }
    }
}
