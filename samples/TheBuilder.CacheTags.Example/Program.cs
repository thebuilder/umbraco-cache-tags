WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
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
