WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
    // CACHE TAGS: call .AddCacheTags() here once src/TheBuilder.CacheTags exists and the csproj
    // references it. Keep it after AddDeliveryApi(), which registers the handlers it builds on.
    .AddComposers()
    .Build();

WebApplication app = builder.Build();
await app.BootUmbracoAsync();
app.UseUmbraco()
    .WithMiddleware(middleware =>
    {
        middleware.UseBackOffice();
        middleware.UseWebsite();
    })
    .WithEndpoints(endpoints =>
    {
        endpoints.UseBackOfficeEndpoints();
        endpoints.UseWebsiteEndpoints();
    });
await app.RunAsync();
