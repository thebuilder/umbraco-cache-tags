#if NET11_0_OR_GREATER
using Examine;
using Examine.Lucene.Providers;
using Microsoft.Extensions.DependencyInjection;
using TheBuilder.CacheTags.Delivery;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Search.Indexing;
using Umbraco.Cms.Search.Provider.Examine.Configuration;
using Umbraco.Cms.Search.Provider.Examine.Lucene;
using Umbraco.Cms.Search.Provider.Examine.Services;
using IndexField = Umbraco.Cms.Core.Search.Indexing.IndexField;
using UmbracoSearch = Umbraco.Cms.Core.Constants.Search;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     The index check on Umbraco 19, against Umbraco Search's own Examine provider (the one Umbraco
///     installs) in memory: documents go in through its indexer, as Umbraco Search writes them, and
///     the check reads them back through its searcher, as the Delivery API does.
/// </summary>
public sealed class LocalDeliveryIndexTests : IDisposable
{
    private const string Alias = UmbracoSearch.IndexAliases.PublishedContent;

    private static readonly Guid Article = Guid.Parse("e0000000-0000-0000-0000-000000000021");
    private static readonly Guid Other = Guid.Parse("e0000000-0000-0000-0000-000000000022");
    private static readonly string?[] Cultures = ["en-US", "da-DK"];

    private static readonly string Now = IndexStamp.For(Published(publishedVersionId: 12));
    private static readonly string Before = IndexStamp.For(Published(publishedVersionId: 10));

    private readonly ServiceProvider _services;
    private readonly Indexer _indexer;
    private readonly Searcher _searcher;

    public LocalDeliveryIndexTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamine();
        services.AddSingleton<LuceneRAMDirectoryFactory>();
        services.AddExamineLuceneIndex<LuceneIndex, LuceneRAMDirectoryFactory>(Alias);

        // The provider's own field types, as its AddExamineSearchProvider configures every index.
        services.ConfigureOptions(typeof(Indexer).Assembly.GetType("Umbraco.Cms.Search.Provider.Examine.Configuration.ConfigureIndexOptions", throwOnError: true)!);
        services.Configure<FieldOptions>(options => options.Fields = []);
        services.AddOptions<ExamineSearchProviderSettings>();
        services.AddOptions<SearcherOptions>();
        services.AddSingleton<IActiveIndexManager, OneIndex>();
        services.AddTransient<Indexer>();
        services.AddTransient<Searcher>();
        _services = services.BuildServiceProvider();
        _indexer = _services.GetRequiredService<Indexer>();
        _searcher = _services.GetRequiredService<Searcher>();

    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task An_item_is_indexed_once_every_culture_was_built_from_its_state_now()
    {
        await IndexAsync(Article, ("en-US", Now), ("da-DK", Now));

        Assert.True(await HasAsync(new() { [Article] = Now }));
    }

    [Fact]
    public async Task One_culture_built_from_an_older_state_has_not_caught_up()
    {
        await IndexAsync(Article, ("en-US", Now), ("da-DK", Before));

        Assert.False(await HasAsync(new() { [Article] = Now }));
    }

    [Fact]
    public async Task A_new_item_waits_until_the_index_has_it()
    {
        await IndexAsync(Other, ("en-US", Now));

        Assert.False(await HasAsync(new() { [Article] = Now, [Other] = Now }));
    }

    [Fact]
    public async Task A_document_built_before_the_package_was_installed_has_not_caught_up()
    {
        await IndexAsync(Article, ("en-US", null));

        Assert.False(await HasAsync(new() { [Article] = Now }));
    }

    [Fact]
    public async Task An_item_that_should_have_no_documents_is_indexed_once_none_is_left()
    {
        await IndexAsync(Article, ("da-DK", Before));

        Assert.False(await HasAsync(new() { [Article] = null }));

        await WriteAsync(() => _indexer.DeleteAsync(Alias, [Article]));

        Assert.True(await HasAsync(new() { [Article] = null }));
    }

    [Fact]
    public async Task An_item_that_does_not_vary_by_culture_is_found_from_every_culture()
    {
        await IndexAsync(Article, (null, Now));

        Assert.True(await HasAsync(new() { [Article] = Now }));
        Assert.False(await HasAsync(new() { [Article] = IndexStamp.For(Published(publishedVersionId: 13)) }));
    }

    [Fact]
    public async Task Protected_content_is_found_too()
    {
        // Members' listings return it, so the check must see it although no member is signed in.
        await IndexAsync(Article, [("en-US", Now)], new ContentProtection([Guid.Parse("a0000000-0000-0000-0000-000000000001")]));

        Assert.True(await HasAsync(new() { [Article] = Now }));
    }

    [Fact]
    public void A_draft_save_leaves_the_published_state_as_it_was()
    {
        // The published index only changes when what is published does, so its stamp must not follow the draft.
        Assert.Equal(Now, IndexStamp.For(Published(publishedVersionId: 12, versionId: 14)));
        Assert.NotEqual(Now, IndexStamp.For(Published(publishedVersionId: 12, sortOrder: 3)));
        Assert.NotEqual(Now, IndexStamp.For(Published(publishedVersionId: 12, path: "-1,1060,1070,1061")));
    }

    [Fact]
    public void Content_under_an_unpublished_ancestor_is_not_in_the_published_index()
    {
        IContent page = Published(publishedVersionId: 12);

        Assert.Equal(Now, IndexStamp.Expected(page, id => Published(publishedVersionId: id)));
        Assert.Null(IndexStamp.Expected(page, id => id == 1060 ? Published(publishedVersionId: id, published: false) : Published(publishedVersionId: id)));
        Assert.Null(IndexStamp.Expected(Published(publishedVersionId: 12, published: false), id => Published(publishedVersionId: id)));
    }

    [Fact]
    public void A_variant_item_needs_a_culture_its_variant_ancestors_publish_too()
    {
        IContent page = Published(publishedVersionId: 12, cultures: ["da-DK"], variant: true);

        Assert.NotNull(IndexStamp.Expected(page, id => Published(publishedVersionId: id, cultures: ["da-DK", "en-US"], variant: true)));
        Assert.Null(IndexStamp.Expected(page, id => Published(publishedVersionId: id, cultures: ["en-US"], variant: true)));
        Assert.NotNull(IndexStamp.Expected(page, id => Published(publishedVersionId: id, cultures: [], variant: false)));
    }

    private Task<bool> HasAsync(Dictionary<Guid, string?> expected)
        => LocalDeliveryIndex.HasAsync(_searcher, expected, Cultures, CancellationToken.None);

    private Task IndexAsync(Guid key, params (string? Culture, string? State)[] documents)
        => IndexAsync(key, documents, protection: null);

    // The fields Umbraco Search's own indexers give every document, and the stamp the Delivery API's index handlers add per culture.
    private async Task IndexAsync(Guid key, (string? Culture, string? State)[] documents, ContentProtection? protection)
    {
        List<IndexField> fields =
        [
            new(UmbracoSearch.FieldNames.Id, new IndexValue { Keywords = [key.ToString("D")] }, null, null),
            new(UmbracoSearch.FieldNames.ParentId, new IndexValue { Keywords = [Guid.Empty.ToString("D")] }, null, null),
            new(UmbracoSearch.FieldNames.PathIds, new IndexValue { Keywords = [key.ToString("D")] }, null, null),
        ];
        fields.AddRange(documents
            .Where(document => document.State is not null)
            .Select(document => new IndexField(IndexStamp.FieldName, new IndexValue { Keywords = [document.State!] }, document.Culture, null)));

        await WriteAsync(() => _indexer.AddOrUpdateAsync(Alias, key, UmbracoObjectTypes.Document, documents.Select(document => new Variation(document.Culture, null)), fields, protection));
    }

    // Written at once, and searchable before the test goes on: a server's index catches up in the background.
    private async Task WriteAsync(Func<Task> write)
    {
        var index = (LuceneIndex)_services.GetRequiredService<IExamineManager>().GetIndex(Alias);
        using (index.WithThreadingMode(IndexThreadingMode.Synchronous))
        {
            await write();
        }

        index.WaitForChanges();
    }

    private static IContent Published(int publishedVersionId, int? versionId = null, int sortOrder = 2, string path = "-1,1060,1061", bool published = true, string[]? cultures = null, bool variant = false)
    {
        return Fake.Of<IContent>((method, _) => method.Name switch
        {
            "get_Published" => published,
            "get_Trashed" => false,
            "get_VersionId" => versionId ?? publishedVersionId,
            "get_PublishedVersionId" => publishedVersionId,
            "get_PublishedCultures" => cultures ?? [],
            "get_Path" => path,
            "get_SortOrder" => sortOrder,
            "get_ContentType" => Fake.Of<ISimpleContentType>((typeMethod, _) => typeMethod.Name == "get_Variations" ? variant ? ContentVariation.Culture : ContentVariation.Nothing : null),
            _ => null,
        });
    }

    private sealed class OneIndex : IActiveIndexManager
    {
        public string ResolveActiveIndexName(string indexAlias) => indexAlias;

        public string ResolveShadowIndexName(string indexAlias) => indexAlias;

        public bool IsRebuilding(string indexAlias) => false;

        public void StartRebuilding(string indexAlias)
        {
        }

        public void CompleteRebuilding(string indexAlias)
        {
        }

        public void CancelRebuilding(string indexAlias)
        {
        }
    }
}
#endif
