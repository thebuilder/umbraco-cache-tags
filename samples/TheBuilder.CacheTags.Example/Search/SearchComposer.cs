using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Example.Search;

/// <summary>Adds full-text search to the Delivery API: <c>content?filter=search:harbour</c>.</summary>
public sealed class SearchComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.ContentIndexHandlers().Add<SearchTextIndexer>();
        builder.FilterHandlers().Add<SearchFilter>();
    }
}
