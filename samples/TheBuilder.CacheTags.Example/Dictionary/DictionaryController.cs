using Microsoft.AspNetCore.Mvc;
using TheBuilder.CacheTags;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Example.Dictionary;

/// <summary>
///     A site's own API, outside the Delivery API: every dictionary item in one language,
///     <c>GET /api/dictionary?locale=da-DK</c>. <see cref="CacheTaggedAttribute" /> tags its responses
///     like Delivery API responses, and each response names the key a dictionary change purges
///     (<see cref="DictionaryCacheTags" />).
/// </summary>
[ApiController]
[Route("api/dictionary")]
[CacheTagged]
public sealed class DictionaryController(IDictionaryItemService dictionaryItems, ILanguageService languages, ICacheTagsResponse cacheTags) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string locale)
    {
        ILanguage? language = await languages.GetAsync(locale);
        if (language is null)
        {
            return NotFound(new { error = $"No language {locale}." });
        }

        cacheTags.Add(DictionaryCacheTags.Key(language.IsoCode));
        IEnumerable<IDictionaryItem> items = await dictionaryItems.GetDescendantsAsync(null);
        return Ok(items
            .OrderBy(item => item.ItemKey, StringComparer.Ordinal)
            .ToDictionary(
                item => item.ItemKey,
                item => item.Translations.FirstOrDefault(translation => translation.LanguageIsoCode == language.IsoCode)?.Value ?? string.Empty));
    }
}
