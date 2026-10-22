// Stand-ins for Umbraco's Delivery API controllers, which the package recognises by namespace and name
// (DeliveryEndpoint.Of): an item by route, items by id, and the content query (listing) endpoint.
namespace Umbraco.Cms.Api.Delivery.Controllers.Content;

internal sealed class ByRouteContentApiController;

internal sealed class ByIdsContentApiController;

internal sealed class QueryContentApiController;
