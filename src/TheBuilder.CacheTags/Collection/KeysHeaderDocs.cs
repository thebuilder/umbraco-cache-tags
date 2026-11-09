using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Documents the keys header on every response <see cref="CacheKeyResultFilter" /> tags, in the
///     OpenAPI documents Umbraco generates, so a typed client sees it. Umbraco 17 generates them with
///     Swashbuckle and Umbraco 18 with Microsoft.AspNetCore.OpenApi. Only the one the site has is
///     configured: the package does not ship either, so the other's types are never loaded.
/// </summary>
internal static class KeysHeaderDocs
{
    public static void Register(IServiceCollection services)
    {
        if (Type.GetType("Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions, Swashbuckle.AspNetCore.SwaggerGen") is not null)
        {
            RegisterSwashbuckle(services);
        }

        if (Type.GetType("Microsoft.AspNetCore.OpenApi.OpenApiOptions, Microsoft.AspNetCore.OpenApi") is not null)
        {
            RegisterAspNetCoreOpenApi(services);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterSwashbuckle(IServiceCollection services)
        => services.AddOptions<SwaggerGenOptions>().Configure<IOptionsMonitor<CacheTagsOptions>>(
            (options, settings) => options.AddOperationFilterInstance(SwashbuckleKeysHeader.Create(settings)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterAspNetCoreOpenApi(IServiceCollection services)
        => services.AddTransient<IConfigureOptions<OpenApiOptions>>(provider => new ConfigureNamedOptions<OpenApiOptions>(
            name: null, // every document, Umbraco's "delivery" included
            options => options.AddOperationTransformer(
                new AspNetCoreOpenApiKeysHeader(provider.GetRequiredService<IOptionsMonitor<CacheTagsOptions>>()).TransformAsync)));

    /// <summary>Whether the result filter tags this action's responses: a Delivery API action, or one with <see cref="CacheTaggedAttribute" />.</summary>
    public static bool Tags(ActionDescriptor action)
        => action is ControllerActionDescriptor controller
           && (DeliveryEndpoint.Of(controller.ControllerTypeInfo) is not null
               || controller.MethodInfo.GetCustomAttribute<CacheTaggedAttribute>() is not null
               || controller.ControllerTypeInfo.GetCustomAttribute<CacheTaggedAttribute>() is not null);

    /// <summary>Adds the header to the operation's 200 response, the only one that carries keys.</summary>
    public static void AddTo(OpenApiOperation operation, CacheTagsOptions settings)
    {
        if (settings.Enabled is false
            || operation.Responses?.TryGetValue("200", out IOpenApiResponse? found) is not true
            || found is not OpenApiResponse response)
        {
            return;
        }

        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
        response.Headers[settings.HeaderName] = new OpenApiHeader
        {
            Description = "The cache keys of what this response shows, comma separated. Tag the cached response with them. "
                + "Absent on preview and signed-in member responses. See https://cache-tags.thebuilder.dk/keys",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
    }
}

/// <summary>
///     The keys header in Umbraco 17's OpenAPI documents, which Swashbuckle generates. A proxy of
///     Swashbuckle's <see cref="IOperationFilter" /> rather than a class that implements it: Umbraco loads
///     every type of the package at startup, and on Umbraco 18, which has no Swashbuckle, a type that
///     implements its interface fails to load and stops the site.
/// </summary>
internal class SwashbuckleKeysHeader : DispatchProxy
{
    private IOptionsMonitor<CacheTagsOptions> _options = null!;

    public static IOperationFilter Create(IOptionsMonitor<CacheTagsOptions> options)
    {
        IOperationFilter filter = Create<IOperationFilter, SwashbuckleKeysHeader>();
        ((SwashbuckleKeysHeader)(object)filter)._options = options;
        return filter;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IOperationFilter.Apply)
            && args is [OpenApiOperation operation, OperationFilterContext context]
            && KeysHeaderDocs.Tags(context.ApiDescription.ActionDescriptor))
        {
            KeysHeaderDocs.AddTo(operation, _options.CurrentValue);
        }

        return null;
    }
}

/// <summary>
///     The keys header in Umbraco 18's OpenAPI documents, which Microsoft.AspNetCore.OpenApi generates.
///     A transformer delegate rather than a class that implements its interface, for the same reason
///     as <see cref="SwashbuckleKeysHeader" /> on Umbraco 17.
/// </summary>
internal sealed class AspNetCoreOpenApiKeysHeader(IOptionsMonitor<CacheTagsOptions> options)
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (KeysHeaderDocs.Tags(context.Description.ActionDescriptor))
        {
            KeysHeaderDocs.AddTo(operation, options.CurrentValue);
        }

        return Task.CompletedTask;
    }
}
