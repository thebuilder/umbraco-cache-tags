using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Collection;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     Which results the filter treats as a 200. It runs before the result does, when the response
///     still says 200 whatever the result will answer.
/// </summary>
public sealed class ResultStatusTests
{
    public static TheoryData<IActionResult> NotOk =>
    [
        // The Delivery API's answer for an item's old route (ByRouteContentApiController.RedirectTo).
        new RedirectResult("/guides/getting-around/on-foot-renamed/", permanent: true),
        new LocalRedirectResult("/elsewhere"),
        new RedirectToRouteResult("route", null),
        new RedirectToActionResult("Get", "Dictionary", null),
        new ChallengeResult(),
        new ForbidResult(),
    ];

    [Theory]
    [MemberData(nameof(NotOk))]
    public void A_redirect_or_challenge_is_not_tagged_as_a_200(IActionResult result)
    {
        (var status, _) = CacheKeyResultFilter.Outcome(result, new DefaultHttpContext().Response);

        Assert.NotEqual(200, status);
    }

    [Fact]
    public void An_ok_result_is_a_200_with_its_value()
    {
        var value = new object();

        (var status, var body) = CacheKeyResultFilter.Outcome(new OkObjectResult(value), new DefaultHttpContext().Response);

        Assert.Equal(200, status);
        Assert.Same(value, body);
    }

    [Fact]
    public void A_status_code_result_answers_its_status()
    {
        (var status, _) = CacheKeyResultFilter.Outcome(new NotFoundResult(), new DefaultHttpContext().Response);

        Assert.Equal(404, status);
    }
}
