using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Infrastructure.DeliveryApi;

namespace TheBuilder.CacheTags.Spike.Probe;

// Strategy A, "render": decorate every Delivery API builder that turns a published item into output,
// and record each item it is called with. Expected to miss items whose property values come from
// Umbraco's property value cache (PropertyCacheLevel.Element / Elements) on warm requests.

internal static class DecorationExtensions
{
    public static void Decorate<TService>(this IServiceCollection services, Func<TService, IServiceProvider, TService> wrap)
        where TService : class
    {
        ServiceDescriptor descriptor = services.LastOrDefault(d => d.ServiceType == typeof(TService) && d.IsKeyedService is false)
            ?? throw new InvalidOperationException($"No registration for {typeof(TService).Name} to decorate.");
        services.Remove(descriptor);

        Func<IServiceProvider, object> inner = descriptor switch
        {
            { ImplementationInstance: { } instance } => _ => instance,
            { ImplementationFactory: { } factory } => factory,
            _ => sp => ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!),
        };

        services.Add(new ServiceDescriptor(typeof(TService), sp => wrap((TService)inner(sp), sp), descriptor.Lifetime));
    }
}

internal abstract class RecordingDecorator(IHttpContextAccessor accessor)
{
    protected const string Strategy = "render";

    protected void Record(IPublishedElement? item) => Collect.Add(accessor.HttpContext, Strategy, item);
}

internal sealed class RecordingContentBuilder(IApiContentBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiContentBuilder
{
    public IApiContent? Build(IPublishedContent content)
    {
        Record(content);
        return inner.Build(content);
    }
}

internal sealed class RecordingContentResponseBuilder(IApiContentResponseBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiContentResponseBuilder
{
    public IApiContentResponse? Build(IPublishedContent content)
    {
        Record(content);
        return inner.Build(content);
    }
}

internal sealed class RecordingContentRouteBuilder(IApiContentRouteBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiContentRouteBuilder
{
    public IApiContentRoute? Build(IPublishedContent content, string? culture = null)
    {
        Record(content);
        return inner.Build(content, culture);
    }
}

internal sealed class RecordingContentNameProvider(IApiContentNameProvider inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiContentNameProvider
{
    public string GetName(IPublishedContent content)
    {
        Record(content);
        return inner.GetName(content);
    }
}

internal sealed class RecordingMediaUrlProvider(IApiMediaUrlProvider inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiMediaUrlProvider
{
    public string GetUrl(IPublishedContent media)
    {
        Record(media);
        return inner.GetUrl(media);
    }
}

internal sealed class RecordingMediaBuilder(IApiMediaBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiMediaBuilder
{
    public IApiMedia Build(IPublishedContent media)
    {
        Record(media);
        return inner.Build(media);
    }
}

internal sealed class RecordingMediaWithCropsBuilder(IApiMediaWithCropsBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiMediaWithCropsBuilder
{
    public IApiMediaWithCrops Build(MediaWithCrops media)
    {
        Record(media.Content);
        return inner.Build(media);
    }

    public IApiMediaWithCrops Build(IPublishedContent media)
    {
        Record(media);
        return inner.Build(media);
    }
}

internal sealed class RecordingMediaWithCropsResponseBuilder(IApiMediaWithCropsResponseBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiMediaWithCropsResponseBuilder
{
    public IApiMediaWithCropsResponse Build(IPublishedContent media)
    {
        Record(media);
        return inner.Build(media);
    }
}

internal sealed class RecordingElementBuilder(IApiElementBuilder inner, IHttpContextAccessor accessor)
    : RecordingDecorator(accessor), IApiElementBuilder
{
    public IApiElement Build(IPublishedElement element)
    {
        Record(element);
        return inner.Build(element);
    }
}
