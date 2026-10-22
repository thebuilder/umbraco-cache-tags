using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

namespace TheBuilder.CacheTags.Example.Dictionary;

public sealed class DictionaryComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
        => builder
            .AddNotificationAsyncHandler<DictionaryItemSavedNotification, DictionaryCacheTags>()
            .AddNotificationAsyncHandler<DictionaryItemDeletedNotification, DictionaryCacheTags>()
            .AddNotificationHandler<DictionaryCacheRefresherNotification, DictionaryCacheTags>()
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DictionarySeeder>();
}
