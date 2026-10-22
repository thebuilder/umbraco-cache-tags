// Stand-ins for Umbraco's Delivery API controllers, which the package recognises by namespace and name
// (DeliveryEndpoint.Of): an item by route, items by id, and the content query (listing) endpoint.
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Tests;

namespace Umbraco.Cms.Api.Delivery.Controllers.Content;

internal sealed class ByRouteContentApiController;

internal sealed class ByIdsContentApiController;

internal sealed class QueryContentApiController;

/// <summary>An item by id, as the Delivery API answers it, for <see cref="DeliveryPipelineTests" />: one item exists.</summary>
[ApiController]
public sealed class ByIdContentApiController : ControllerBase
{
    [HttpGet("/umbraco/delivery/api/v2/content/item/{id:guid}")]
    public IActionResult ById(Guid id)
        => id == new Guid("e0000000-0000-0000-0000-000000000003")
            ? Ok(new { id, contentType = "article", name = "Harbour mornings" })
            : NotFound();
}
