using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>Admin-only answers to "what did this change purge, and has it gone out?", for the backoffice view.</summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsChangesController(ICacheTagsChangeLog changeLog) : ControllerBase
{
    /// <summary>The latest changes, newest first: what each planned, who applied and indexed it, and its purges.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes([FromQuery] int take = 20, CancellationToken cancellationToken = default)
        => Ok(await changeLog.ChangesAsync(take, cancellationToken));
}
