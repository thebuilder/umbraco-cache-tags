// Actions for KeysHeaderDocsTests besides the Delivery API stand-in: one the package tags with
// [CacheTagged], and one it leaves alone.
using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags;

namespace TheBuilder.CacheTags.Tests;

[ApiController]
public sealed class SiteApiController : ControllerBase
{
    [CacheTagged]
    [HttpGet("/api/dictionary")]
    public IActionResult Dictionary() => Ok(new { hello = "Hej" });

    [HttpGet("/api/health")]
    public IActionResult Health() => Ok(new { healthy = true });
}
