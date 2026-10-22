using TheBuilder.CacheTags;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Example.Dictionary;

/// <summary>
///     Purges the dictionary API's responses (<see cref="DictionaryController" />) when an item
///     changes. The change waits for the <c>dictionary</c> signal, which every server reports once its
///     dictionary cache is refreshed, so a frontend never refetches a dictionary a server has not got yet.
/// </summary>
public sealed class DictionaryCacheTags(ICacheTagsChanges changes, ILanguageService languages)
    : INotificationAsyncHandler<DictionaryItemSavedNotification>,
        INotificationAsyncHandler<DictionaryItemDeletedNotification>,
        INotificationHandler<DictionaryCacheRefresherNotification>
{
    public const string Signal = "dictionary";

    public static string Key(string isoCode) => $"x-dictionary-{isoCode.ToLowerInvariant()}";

    public Task HandleAsync(DictionaryItemSavedNotification notification, CancellationToken cancellationToken)
        => RecordAsync("dictionary items saved");

    public Task HandleAsync(DictionaryItemDeletedNotification notification, CancellationToken cancellationToken)
        => RecordAsync("dictionary items deleted");

    // Raised on every server, once its dictionary cache has the change.
    public void Handle(DictionaryCacheRefresherNotification notification) => changes.Applied(Signal);

    // A dictionary item has text in every language, so a change reaches each language's response.
    private async Task RecordAsync(string reason)
        => changes.Record((await languages.GetAllAsync()).Select(language => Key(language.IsoCode)), Signal, reason: reason);
}
