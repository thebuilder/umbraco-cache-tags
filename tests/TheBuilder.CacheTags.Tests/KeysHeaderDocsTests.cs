using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     The keys header in the OpenAPI document of the Umbraco release the tests build against: Umbraco
///     17 generates it with Swashbuckle and Umbraco 18 with Microsoft.AspNetCore.OpenApi, and CI runs
///     the tests on both. The document comes from the generator's own endpoint, with the registration
///     CacheTagsComposer makes.
/// </summary>
public sealed class KeysHeaderDocsTests
{
    private const string DeliveryItem = "/umbraco/delivery/api/v2/content/item/{id}";

    private static readonly bool Swashbuckle =
        Type.GetType("Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions, Swashbuckle.AspNetCore.SwaggerGen") is not null;

    [Fact]
    public async Task A_delivery_api_item_documents_the_keys_header_on_its_200_response()
    {
        JsonElement header = Header(await DocumentAsync(new CacheTagsOptions()), DeliveryItem)!.Value;

        Assert.Equal("string", header.GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("cache keys", header.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cache_tagged_action_documents_the_header_and_any_other_action_does_not()
    {
        JsonDocument document = await DocumentAsync(new CacheTagsOptions());

        Assert.NotNull(Header(document, "/api/dictionary"));
        Assert.Null(Header(document, "/api/health"));
    }

    [Fact]
    public async Task The_header_is_documented_under_the_configured_name()
    {
        JsonDocument document = await DocumentAsync(new CacheTagsOptions { HeaderName = "Cache-Tag" });

        Assert.NotNull(Header(document, DeliveryItem, "Cache-Tag"));
        Assert.Null(Header(document, DeliveryItem));
    }

    [Fact]
    public async Task With_the_package_turned_off_no_header_is_documented()
    {
        JsonDocument document = await DocumentAsync(new CacheTagsOptions { Enabled = false });

        Assert.Null(Header(document, DeliveryItem));
    }

    private static JsonElement? Header(JsonDocument document, string path, string name = "X-Umbraco-Cache-Keys")
    {
        JsonElement ok = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200");
        return ok.TryGetProperty("headers", out JsonElement headers) && headers.TryGetProperty(name, out JsonElement header) ? header : null;
    }

    private static async Task<JsonDocument> DocumentAsync(CacheTagsOptions settings)
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IOptionsMonitor<CacheTagsOptions>>(new TestOptions(settings));
                    services.AddControllers().ConfigureApplicationPartManager(parts =>
                    {
                        parts.ApplicationParts.Clear();
                        parts.ApplicationParts.Add(new AssemblyPart(typeof(KeysHeaderDocsTests).Assembly));
                    });
                    if (Swashbuckle)
                    {
                        AddSwashbuckle(services);
                    }
                    else
                    {
                        AddAspNetCoreOpenApi(services);
                    }

                    // As CacheTagsComposer registers it.
                    KeysHeaderDocs.Register(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    if (Swashbuckle)
                    {
                        UseSwashbuckle(app);
                    }
                    else
                    {
                        app.UseEndpoints(endpoints => MapAspNetCoreOpenApi(endpoints));
                    }
                }))
            .StartAsync();

        using HttpClient client = host.GetTestClient();
        var json = await client.GetStringAsync(Swashbuckle ? "/swagger/delivery/swagger.json" : "/openapi/delivery.json");
        await host.StopAsync();
        return JsonDocument.Parse(json);
    }

    // Each generator in a method of its own, so the one this Umbraco release lacks is never loaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddSwashbuckle(IServiceCollection services)
        => services.AddSwaggerGen(options => options.SwaggerDoc("delivery", new Microsoft.OpenApi.OpenApiInfo { Title = "Delivery", Version = "v1" }));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UseSwashbuckle(IApplicationBuilder app) => app.UseSwagger();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddAspNetCoreOpenApi(IServiceCollection services) => services.AddOpenApi("delivery");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MapAspNetCoreOpenApi(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) => endpoints.MapOpenApi();
}

/// <summary>
///     Umbraco loads every type of the package at startup. The package documents its header for both
///     OpenAPI generators but ships neither, so on each Umbraco release one of them is missing, and no
///     type of the package may need it to load.
/// </summary>
public sealed class PackageTypesTests
{
    [Fact]
    public void Every_type_of_the_package_loads_without_the_generator_this_umbraco_lacks()
    {
        Type[] types = typeof(KeysHeaderDocs).Assembly.GetTypes();

        Assert.Contains(typeof(KeysHeaderDocs), types);
    }
}
