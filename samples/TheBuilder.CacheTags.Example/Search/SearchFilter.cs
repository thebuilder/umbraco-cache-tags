using Umbraco.Cms.Core.DeliveryApi;

namespace TheBuilder.CacheTags.Example.Search;

/// <summary>
///     The Delivery API filter <c>search:&lt;words&gt;</c>: items whose text (<see cref="SearchTextIndexer" />)
///     has a word starting with any of the words. Send one filter per word to require all of them:
///     <c>content?filter=search:harbour&amp;filter=search:morning</c>.
/// </summary>
internal sealed class SearchFilter : IFilterHandler
{
    private const string Specifier = "search:";

    public bool CanHandle(string query) => query.StartsWith(Specifier, StringComparison.OrdinalIgnoreCase);

    public FilterOption BuildFilterOption(string filter)
        => new()
        {
            FieldName = SearchTextIndexer.FieldName,
            Values = filter[Specifier.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Operator = FilterOperation.Contains,
        };
}
