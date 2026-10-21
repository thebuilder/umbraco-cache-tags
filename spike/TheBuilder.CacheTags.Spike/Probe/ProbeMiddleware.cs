using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Spike.Probe;

/// <summary>
///     Runs inside MVC after a Delivery API action: walks the result objects, reads the items the
///     controller stashed for Umbraco's output cache, computes their references, and stamps a header
///     so a response replayed from the output cache can be recognised.
/// </summary>
internal sealed class ProbeResultFilter : IAsyncResultFilter
{
    // Copied from Umbraco.Cms.Api.Delivery.Caching.DeliveryApiOutputCacheKeys (internal, 17.4+).
    private const string ResolvedContentItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedContentItems";
    private const string ResolvedMediaItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedMediaItems";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        HttpContext http = context.HttpContext;
        if (ProbeMiddleware.IsDeliveryApi(http.Request) is false)
        {
            await next();
            return;
        }

        http.Items["spike.ran"] = true;
        http.Response.Headers["X-Spike-Rendered"] = $"{DateTime.UtcNow:O}";
        IServiceProvider services = http.RequestServices;
        http.Response.Headers["X-Spike-Preview"] = services.GetRequiredService<IRequestPreviewService>().IsPreview().ToString();

        if (context.Result is ObjectResult { Value: { } value })
        {
            var objects = new HashSet<string>();
            ObjectWalker.Walk(value, objects);
            foreach (var key in objects)
            {
                Collect.Add(http, "objects", key);
            }
        }

        var resolved = new List<IPublishedContent>();
        if (http.Items[ResolvedContentItemsKey] is IPublishedContent[] content)
        {
            resolved.AddRange(content);
        }

        if (http.Items[ResolvedMediaItemsKey] is IPublishedContent[] media)
        {
            resolved.AddRange(media);
        }

        PropertyEditorCollection editors = services.GetRequiredService<PropertyEditorCollection>();
        DataValueReferenceFactoryCollection factories = services.GetRequiredService<DataValueReferenceFactoryCollection>();
        IPublishedContentCache contentCache = services.GetRequiredService<IPublishedContentCache>();
        IPublishedMediaCache mediaCache = services.GetRequiredService<IPublishedMediaCache>();
        var culture = services.GetRequiredService<IVariationContextAccessor>().VariationContext?.Culture;
        http.Response.Headers["X-Spike-Culture"] = culture ?? string.Empty;

        foreach (IPublishedContent item in resolved)
        {
            Collect.Add(http, "resolved", item);
            Collect.Add(http, "refs", item);
            Collect.Add(http, "refsCulture", item);
            foreach (var reference in ReferenceWalker.References(item, editors, factories, culture))
            {
                Collect.Add(http, "refsCulture", reference);
            }

            foreach (var reference in ReferenceWalker.References(item, editors, factories))
            {
                Collect.Add(http, "refs", reference);

                // Depth 2: what the referenced item itself references (reached through expansion).
                IPublishedContent? referenced = reference[0] switch
                {
                    'c' => await contentCache.GetByIdAsync(Guid.Parse(reference[2..])),
                    'm' => await mediaCache.GetByIdAsync(Guid.Parse(reference[2..])),
                    _ => null,
                };
                if (referenced is null)
                {
                    continue;
                }

                foreach (var second in ReferenceWalker.References(referenced, editors, factories))
                {
                    Collect.Add(http, "refs2", second);
                }
            }
        }

        await next();
    }
}

/// <summary>
///     Sits in front of Umbraco, so it also sees responses the output cache serves. Walks the JSON
///     body and writes one <see cref="ProbeEntry" /> per Delivery API request.
/// </summary>
internal sealed class ProbeMiddleware(RequestDelegate next)
{
    public static bool IsDeliveryApi(HttpRequest request) => request.Path.StartsWithSegments("/umbraco/delivery/api");

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsDeliveryApi(context.Request) is false)
        {
            await next(context);
            return;
        }

        Stream original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var body = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var richText = body.Contains("\"tag\":\"#root\"", StringComparison.Ordinal) ? "json"
            : body.Contains("\"markup\":", StringComparison.Ordinal) ? "markup"
            : "none";
        buffer.Position = 0;
        if (context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) is true && buffer.Length > 0)
        {
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(buffer);
                var json = new HashSet<string>();
                JsonWalker.Walk(document.RootElement, json);
                foreach (var key in json)
                {
                    Collect.Add(context, "json", key);
                }
            }
            catch (JsonException)
            {
                // Not JSON after all; nothing to collect.
            }
        }

        Dictionary<string, HashSet<string>> sets = Collect.Sets(context);
        ProbeLog.Requests.Enqueue(new ProbeEntry(
            ProbeLog.Next(),
            context.Request.Headers["X-Spike-Label"].ToString(),
            context.Request.Path,
            context.Request.QueryString.ToString(),
            context.Response.StatusCode,
            context.Items.ContainsKey("spike.ran"),
            context.Response.Headers["X-Spike-Rendered"].ToString(),
            richText,
            new[] { "Cache-Control", "Vary", "X-Spike-Preview", "X-Spike-Culture" }
                .ToDictionary(name => name, name => context.Response.Headers[name].ToString()),
            sets.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray())));

        buffer.Position = 0;
        await buffer.CopyToAsync(original);
    }
}
