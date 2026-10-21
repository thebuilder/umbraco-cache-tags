using TheBuilder.CacheTags.Spike.Probe;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();
await app.BootUmbracoAsync();

// Outside Umbraco's pipeline, so it also sees responses served from the output cache.
app.UseMiddleware<ProbeMiddleware>();
app.Map("/spike", spike => spike.Run(SpikeEndpoints.HandleAsync));

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
