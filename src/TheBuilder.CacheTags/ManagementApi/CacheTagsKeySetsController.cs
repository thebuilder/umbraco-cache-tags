using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>Admin-only answers to "how many stored key sets hold this item?", for the Cache box on an item's Info tab.</summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute(Constants.ApiRoot)]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[Authorize(Policy = AdministratorRequirement.Policy)]
[MapToApi(Constants.ApiName)]
[ApiExplorerSettings(GroupName = Constants.PackageName)]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CacheTagsKeySetsController(ICacheTagsKeySets keySets) : ControllerBase
{
    /// <summary>How many stored key sets hold the item with this <c>content-</c> or <c>media-</c> key, so a purge of it also reaches them.</summary>
    [HttpGet("keysets")]
    public IActionResult KeySets([FromQuery] string key)
        => CacheKeys.IsItem(key) && CacheKeys.IsNameable(key)
            ? Ok(new { key, sets = keySets.CountOf(key) })
            : BadRequest(new { error = $"\"{key}\" is not an item's content- or media- key." });
}

/// <summary>
///     What the Cache box reads about key sets. Public only because the controller's constructor takes it; its
///     members are internal, so nothing outside the package can implement or call it.
/// </summary>
public interface ICacheTagsKeySets
{
    internal int CountOf(string itemKey);
}

internal sealed class CacheTagsKeySets(KeySetStore store) : ICacheTagsKeySets
{
    public int CountOf(string itemKey) => store.CountOf(itemKey);
}
