using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     Admin-only answers to "what does this key stand for?" and "what did this change purge?", for the
///     backoffice view.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsLookupController(ICacheTagsLookup lookup) : ControllerBase
{
    /// <summary>
    ///     A <c>response-</c> key: the request it stands for and the keys it covers. Any other key: the
    ///     responses that cover it.
    /// </summary>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string key, CancellationToken cancellationToken)
        => await lookup.LookupAsync(key, cancellationToken) is { } result
            ? Ok(result)
            : NotFound(new { key, error = "No response with this key has been rendered, or it has aged out." });

    /// <summary>
    ///     Searches for keys without knowing them exactly: documents and media by name, and recorded
    ///     keys and responses whose key, path or query contain the text.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken cancellationToken)
        => q is { Length: >= 2 and <= 200 }
            ? Ok(await lookup.SearchAsync(q.Trim(), cancellationToken))
            : BadRequest(new { error = "Search for 2 to 200 characters." });

    /// <summary>The latest changes, newest first: what each planned, the responses it reaches now, who acknowledged it, and its purges.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes([FromQuery] int take = 20, CancellationToken cancellationToken = default)
        => Ok(await lookup.ChangesAsync(take, cancellationToken));
}
