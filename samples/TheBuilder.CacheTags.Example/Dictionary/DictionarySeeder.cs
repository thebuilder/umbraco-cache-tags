using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Example.Dictionary;

/// <summary>Seeds the dictionary item the Next.js footer shows, in English and Danish.</summary>
public sealed class DictionarySeeder(IRuntimeState runtimeState, IDictionaryItemService dictionaryItems, ILanguageService languages)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    /// <summary>The seeded item's key (the e7 range, see Seeding/ExampleContent.cs).</summary>
    public static readonly Guid FooterTagline = new("e7000000-0000-0000-0000-000000000001");

    public const string FooterTaglineKey = "Footer.Tagline";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || await dictionaryItems.GetAsync(FooterTagline) is not null)
        {
            return;
        }

        var texts = new Dictionary<string, string>
        {
            ["en-US"] = "Built to check that cached pages refresh exactly when they should.",
            ["da-DK"] = "Bygget til at tjekke, at cachede sider opdateres præcis når de skal.",
        };
        var translations = (await languages.GetAllAsync())
            .Where(language => texts.ContainsKey(language.IsoCode))
            .Select(language => (IDictionaryTranslation)new DictionaryTranslation(language, texts[language.IsoCode]))
            .ToList();
        await dictionaryItems.CreateAsync(new DictionaryItem(FooterTaglineKey) { Key = FooterTagline, Translations = translations }, Constants.Security.SuperUserKey);
    }
}
