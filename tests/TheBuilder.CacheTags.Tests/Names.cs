using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Collection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Tests;

/// <summary>A site's content types, media types and languages, as <see cref="KnownNames" /> reads them.</summary>
internal static class Names
{
    public static KnownNames Of(string[] contentTypes, string[]? mediaTypes = null, string[]? cultures = null) => new(
        Fake<IContentTypeService>.Of((method, _) => method.Name == "GetAll" ? contentTypes.Select(alias => Fake<IContentType>.Of((m, _) => m.Name == "get_Alias" ? alias : null)).ToArray() : null),
        Fake<IMediaTypeService>.Of((method, _) => method.Name == "GetAll" ? (mediaTypes ?? []).Select(alias => Fake<IMediaType>.Of((m, _) => m.Name == "get_Alias" ? alias : null)).ToArray() : null),
        Fake<ILanguageService>.Of((method, _) => method.Name == "GetAllAsync"
            ? Task.FromResult((cultures ?? []).Select(culture => Fake<ILanguage>.Of((m, _) => m.Name == "get_IsoCode" ? culture : null)))
            : null),
        TimeProvider.System,
        NullLogger<KnownNames>.Instance);
}
