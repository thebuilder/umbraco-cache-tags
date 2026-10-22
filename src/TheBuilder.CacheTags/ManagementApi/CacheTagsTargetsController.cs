using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Targets;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     Admin-only: the purge targets, adding, changing and removing those kept in the backoffice, and
///     sending any target a test purge.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsTargetsController(ICacheTagsTargets targets) : ControllerBase
{
    /// <summary>Every target, from configuration and the backoffice, and the registered target types.</summary>
    [HttpGet("targets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult List() => Ok(targets.List());

    /// <summary>Adds a target to the backoffice.</summary>
    [HttpPost("targets")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Add([FromBody] JsonElement body)
        => Read(body) is not { } target
            ? BadRequest(new { error = "The body is not a target." })
            : Result(targets.Add(target), StatusCodes.Status201Created);

    /// <summary>Changes a backoffice target. Its name never changes.</summary>
    [HttpPut("targets/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Update(string name, [FromBody] JsonElement body)
        => Read(body) is not { } target
            ? BadRequest(new { error = "The body is not a target." })
            : Result(targets.Update(name, target), StatusCodes.Status204NoContent);

    /// <summary>Removes a backoffice target, setting its unsent purges aside.</summary>
    [HttpDelete("targets/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(string name) => targets.Delete(name) ? NoContent() : NotFound();

    /// <summary>Sends a target, saved or not, a test purge of one key nothing carries, and says whether it was accepted.</summary>
    [HttpPost("targets/test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Test([FromBody] JsonElement body, CancellationToken cancellationToken)
        => Read(body) is not { } target
            ? BadRequest(new { error = "The body is not a target." })
            : Ok(await targets.TestAsync(target, cancellationToken));

    private IActionResult Result(TargetSaveResult result, int success)
        => result switch
        {
            { Found: false } => NotFound(),
            { Error: { } error } => BadRequest(new { error }),
            _ => StatusCode(success),
        };

    private static TargetDefinition? Read(JsonElement body)
    {
        try
        {
            return body.Deserialize(TargetJson.Default.TargetDefinition);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
