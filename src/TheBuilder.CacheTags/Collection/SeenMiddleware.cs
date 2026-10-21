using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Marks a response as served whenever its key goes out, including when Umbraco's output cache
///     replays it and nothing of the package runs inside. Without this, a mapping could age out while
///     a frontend still holds the response, and that response would never be purged.
/// </summary>
internal sealed class SeenMiddleware(RequestDelegate next, MappingWriter writer, ILogger<SeenMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Request.Path.StartsWithSegments("/umbraco/delivery/api", StringComparison.OrdinalIgnoreCase) is false)
        {
            return;
        }

        foreach (var key in context.Response.Headers[Constants.HeaderName].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var start = key.IndexOf(CacheKeys.ResponsePrefix, StringComparison.Ordinal);
            if (start >= 0)
            {
                logger.LogDebug("Delivery API response {ResponseKey} served.", key[start..]);
                writer.Touch(key[start..]);
            }
        }
    }
}

/// <summary>
///     Puts <see cref="SeenMiddleware" /> after routing and before Umbraco's output cache middleware.
/// </summary>
internal static class SeenPipeline
{
    public static UmbracoPipelineFilter Filter() => new(Constants.PackageName)
    {
        PostRouting = app => app.UseMiddleware<SeenMiddleware>(),
    };
}
