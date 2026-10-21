using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Spike.Seeding;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.DeliveryApi;

namespace TheBuilder.CacheTags.Spike.Probe;

/// <summary>
///     Local-only endpoints the driver script calls: read the probe log, and change content the way
///     an editor would, labelled so refresher entries can be matched to the operation.
/// </summary>
internal static class SpikeEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task HandleAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        var path = context.Request.Path.Value?.Trim('/') ?? string.Empty;
        object result = path switch
        {
            "fixture" => Fixture.Describe(),
            "log" => new { requests = ProbeLog.Requests.ToArray(), refreshers = ProbeLog.Refreshers.ToArray() },
            "clear" => Clear(),
            "op" => Operate(services, context.Request.Query["name"].ToString()),
            _ => new { error = "unknown" },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, Json));
    }

    private static object Clear()
    {
        ProbeLog.Clear();
        return new { cleared = true };
    }

    private static object Operate(IServiceProvider services, string name)
    {
        ProbeLog.CurrentOperation = name;

        // Editors change content inside a request with an Umbraco context; without one, redirect
        // tracking logs errors on renames and moves.
        using UmbracoContextReference umbracoContext = services.GetRequiredService<IUmbracoContextFactory>().EnsureUmbracoContext();
        IContentService contentService = services.GetRequiredService<IContentService>();
        IMediaService mediaService = services.GetRequiredService<IMediaService>();
        IContent Get(Guid key) => contentService.GetById(key) ?? throw new InvalidOperationException($"No content {key}.");
        var stamp = DateTime.UtcNow.ToString("HHmmssfff");

        object outcome;
        switch (name)
        {
            case "save-a":
                {
                    IContent a = Get(Fixture.A);
                    a.Name = "Target A draft " + stamp;
                    outcome = contentService.Save(a).Result.ToString() ?? string.Empty;
                    break;
                }

            case "publish-a":
                {
                    IContent a = Get(Fixture.A);
                    a.Name = "Target A";
                    contentService.Save(a);
                    outcome = contentService.Publish(a, ["*"]).Result.ToString();
                    break;
                }

            case "touch-page":
                {
                    // Republishing the page replaces its published item, which drops the converted
                    // values cached on it (PropertyCacheLevel.Element), so the next request is cold.
                    IContent page = Get(Fixture.Page);
                    page.Name = "Fixture";
                    contentService.Save(page);
                    outcome = contentService.Publish(page, ["*"]).Result.ToString();
                    break;
                }

            case "publish-g":
                {
                    IContent g = Get(Fixture.G);
                    g.Name = "Target G " + stamp;
                    contentService.Save(g);
                    outcome = contentService.Publish(g, ["*"]).Result.ToString();
                    break;
                }

            case "publish-f":
                {
                    IContent f = Get(Fixture.F);
                    f.Name = "Target F " + stamp;
                    contentService.Save(f);
                    outcome = contentService.Publish(f, ["*"]).Result.ToString();
                    break;
                }

            case "rename-c":
                {
                    // Changes C's URL segment, so the routes of its descendants change too.
                    IContent c = Get(Fixture.C);
                    c.Name = "Target C " + stamp;
                    contentService.Save(c);
                    outcome = contentService.Publish(c, ["*"]).Result.ToString();
                    break;
                }

            case "unpublish-b":
                outcome = contentService.Unpublish(Get(Fixture.B)).Result.ToString();
                break;

            case "republish-b":
                outcome = contentService.Publish(Get(Fixture.B), ["*"]).Result.ToString();
                break;

            case "move-mover-under-c":
                outcome = contentService.Move(Get(Fixture.Mover), Get(Fixture.C).Id).Result.ToString() ?? string.Empty;
                break;

            case "sort-site":
                {
                    IContent[] children = contentService.GetPagedChildren(Get(Fixture.Site).Id, 0, 100, out _).Reverse().ToArray();
                    outcome = contentService.Sort(children).Result.ToString() ?? string.Empty;
                    break;
                }

            case "publish-variant-da":
                {
                    IContent variant = Get(Fixture.Variant);
                    variant.SetCultureName("Variant DA " + stamp, "da-DK");
                    contentService.Save(variant);
                    outcome = contentService.Publish(variant, ["da-DK"]).Result.ToString();
                    break;
                }

            case "unpublish-variant-da":
                outcome = contentService.Unpublish(Get(Fixture.Variant), "da-DK").Result.ToString();
                break;

            case "save-m1":
                {
                    IMedia m1 = mediaService.GetById(Fixture.M1) ?? throw new InvalidOperationException("No M1.");
                    m1.Name = "Image 1 " + stamp;
                    outcome = mediaService.Save(m1).Success.ToString();
                    break;
                }

            case "trash-d":
                outcome = contentService.MoveToRecycleBin(Get(Fixture.D)).Result.ToString() ?? string.Empty;
                break;

            case "reset":
                {
                    // Undo what the previous run's operations did to the fixture.
                    IContent site = Get(Fixture.Site);
                    foreach (Guid key in new[] { Fixture.D, Fixture.Mover })
                    {
                        IContent item = Get(key);
                        if (item.ParentId != site.Id)
                        {
                            contentService.Move(item, site.Id);
                        }
                    }

                    foreach (Guid key in new[] { Fixture.B, Fixture.D, Fixture.Mover, Fixture.Variant })
                    {
                        contentService.Publish(Get(key), ["*"]);
                    }

                    // A child under C, so renaming C changes a descendant's route.
                    IContent c = Get(Fixture.C);
                    c.Name = "Target C";
                    contentService.Save(c);
                    contentService.Publish(c, ["*"]);
                    if (contentService.GetById(Fixture.ChildOfC) is null)
                    {
                        IContent child = contentService.Create("Child of C", c.Id, "spikePage");
                        child.Key = Fixture.ChildOfC;
                        contentService.Save(child);
                    }

                    contentService.Publish(Get(Fixture.ChildOfC), ["*"]);

                    outcome = "reset";
                    break;
                }

            default:
                outcome = "unknown operation";
                break;
        }

        ProbeLog.CurrentOperation = "idle";
        return new { name, outcome };
    }
}

public sealed class SpikeComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IServiceCollection services = builder.Services;
        services.Decorate<IApiContentBuilder>((inner, sp) => new RecordingContentBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiContentResponseBuilder>((inner, sp) => new RecordingContentResponseBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiContentRouteBuilder>((inner, sp) => new RecordingContentRouteBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiContentNameProvider>((inner, sp) => new RecordingContentNameProvider(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiMediaUrlProvider>((inner, sp) => new RecordingMediaUrlProvider(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiMediaBuilder>((inner, sp) => new RecordingMediaBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiMediaWithCropsBuilder>((inner, sp) => new RecordingMediaWithCropsBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiMediaWithCropsResponseBuilder>((inner, sp) => new RecordingMediaWithCropsResponseBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));
        services.Decorate<IApiElementBuilder>((inner, sp) => new RecordingElementBuilder(inner, sp.GetRequiredService<IHttpContextAccessor>()));

        services.Configure<MvcOptions>(options => options.Filters.Add(new ProbeResultFilter()));

        builder.AddNotificationAsyncHandler<ContentCacheRefresherNotification, RefresherProbe>();
        builder.AddNotificationAsyncHandler<MediaCacheRefresherNotification, RefresherProbe>();
        builder.AddNotificationHandler<ContentTreeChangeNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentPublishedNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentSavedNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentUnpublishedNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentSortedNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentMovedNotification, SourceProbe>();
        builder.AddNotificationHandler<ContentMovedToRecycleBinNotification, SourceProbe>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, FixtureSeeder>();
    }
}
