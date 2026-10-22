using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     Admin-only purging by hand, and the outbox: purges that failed, sent again or set aside.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsPurgesController(ICacheTagsChanges changes, ICacheTagsOutbox outbox, IBackOfficeSecurityAccessor security) : ControllerBase
{
    /// <summary>
    ///     Purges the given keys on every target, at once: custom keys (<c>x-</c>), items'
    ///     <c>content-</c> or <c>media-</c> keys, or <c>umbraco</c> for everything.
    /// </summary>
    [HttpPost("purge")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Purge([FromBody] string[] keys)
    {
        try
        {
            var changeId = changes.Record(keys, reason: $"purged by {security.BackOfficeSecurity?.CurrentUser?.Name ?? "an administrator"}");
            return Accepted(new { changeId });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>The purges in a state (Pending, Sent, Dead or Discarded), newest first.</summary>
    [HttpGet("purges")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Purges([FromQuery] string state = OutboxStates.Dead, [FromQuery] int take = 50)
        => CacheTagsOutbox.States.Contains(state)
            ? Ok(outbox.Purges(CacheTagsOutbox.States.First(known => string.Equals(known, state, StringComparison.OrdinalIgnoreCase)), take))
            : BadRequest(new { error = $"state must be one of {string.Join(", ", CacheTagsOutbox.States)}." });

    /// <summary>Sends a dead or discarded purge again, from its first attempt.</summary>
    [HttpPost("purges/{id:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Retry(Guid id) => outbox.Retry(id) ? NoContent() : NotFound(new { id, error = "No dead or discarded purge has this id." });

    /// <summary>Sets a pending or dead purge aside, so it is not sent.</summary>
    [HttpPost("purges/{id:guid}/discard")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Discard(Guid id) => outbox.Discard(id) ? NoContent() : NotFound(new { id, error = "No pending or dead purge has this id." });
}
