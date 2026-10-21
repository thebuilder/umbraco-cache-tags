using SixLabors.ImageSharp.PixelFormats;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using static TheBuilder.CacheTags.Example.Seeding.SeedValues;

namespace TheBuilder.CacheTags.Example.Seeding;

public sealed class ExampleSeederComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddTransient<SeedWriter>();

        // Handlers run in this order: ExampleSiteSeeder builds on what ExampleSeeder created.
        builder
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ExampleSeeder>()
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ExampleSiteSeeder>()
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ExampleApiUserSeeder>();
    }
}

/// <summary>
///     Seeds a small site on first start: a home page with a block list, an article listing with three
///     articles that pick each other and an image each, and an about page in en-US and da-DK. Skips a
///     database that has the Home content type. <see cref="ExampleSiteSeeder" /> adds the rest.
/// </summary>
internal sealed class ExampleSeeder(
    IRuntimeState runtimeState,
    IContentTypeService contentTypeService,
    IContentService contentService,
    ILanguageService languageService,
    SeedWriter seed,
    IShortStringHelper shortStringHelper,
    ILogger<ExampleSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string Danish = "da-DK";
    private const string English = "en-US";
    private static readonly Guid User = SeedWriter.User;

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || contentTypeService.Get(ExampleContent.HomeType) is not null)
        {
            return;
        }

        logger.LogInformation("Seeding the cache tags example content.");

        if (await languageService.GetAsync(Danish) is null)
        {
            await languageService.CreateAsync(new Language(Danish, "Danish"), User);
        }

        IDataType text = await seed.BuiltInDataType(Constants.DataTypes.Guids.TextstringGuid);
        IDataType textArea = await seed.BuiltInDataType(Constants.DataTypes.Guids.TextareaGuid);
        IDataType contentPicker = await seed.EnsureDataType(null, "Example Content Picker", Constants.PropertyEditors.Aliases.ContentPicker, "Umb.PropertyEditorUi.DocumentPicker", ValueStorageType.Nvarchar);
        IDataType articlePicker = await seed.EnsureDataType(
            null,
            "Example Article Picker",
            Constants.PropertyEditors.Aliases.MultiNodeTreePicker,
            "Umb.PropertyEditorUi.ContentPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["filter"] = ExampleContent.ArticleType.ToString() });
        IDataType imagePicker = await seed.EnsureDataType(
            null,
            "Example Image Picker",
            Constants.PropertyEditors.Aliases.MediaPicker3,
            "Umb.PropertyEditorUi.MediaPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["multiple"] = false });
        IDataType richText = await seed.EnsureDataType(null, "Example Rich Text", Constants.PropertyEditors.Aliases.RichText, "Umb.PropertyEditorUi.Tiptap", ValueStorageType.Ntext);

        var teaser = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.TeaserBlockType, Alias = "teaserBlock", Name = "Teaser", Icon = "icon-navigation-right", IsElement = true,
        };
        teaser.AddPropertyType(new PropertyType(shortStringHelper, text, "heading") { Name = "Heading" }, "content", "Content");
        teaser.AddPropertyType(new PropertyType(shortStringHelper, textArea, "text") { Name = "Text" }, "content", "Content");
        teaser.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "link") { Name = "Link" }, "content", "Content");
        await seed.EnsureContentType(teaser.Key, () => teaser);

        IDataType teasers = await seed.EnsureDataType(
            null,
            "Example Teasers",
            Constants.PropertyEditors.Aliases.BlockList,
            "Umb.PropertyEditorUi.BlockList",
            ValueStorageType.Ntext,
            new Dictionary<string, object>
            {
                ["blocks"] = new[] { new Dictionary<string, object> { ["contentElementTypeKey"] = ExampleContent.TeaserBlockType, ["label"] = "{umbValue: heading}" } },
            });

        var article = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.ArticleType, Alias = "article", Name = "Article", Icon = "icon-article",
        };
        article.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title" }, "content", "Content");
        article.AddPropertyType(new PropertyType(shortStringHelper, textArea, "summary") { Name = "Summary" }, "content", "Content");
        article.AddPropertyType(new PropertyType(shortStringHelper, imagePicker, "image") { Name = "Image" }, "content", "Content");
        article.AddPropertyType(new PropertyType(shortStringHelper, richText, "body") { Name = "Body" }, "content", "Content");
        article.AddPropertyType(new PropertyType(shortStringHelper, articlePicker, "related") { Name = "Related articles" }, "content", "Content");
        await seed.EnsureContentType(article.Key, () => article);

        var articleList = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.ArticleListType, Alias = "articleList", Name = "Article List", Icon = "icon-documents",
            AllowedContentTypes = [new ContentTypeSort(ExampleContent.ArticleType, 0, article.Alias)],
        };
        articleList.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title" }, "content", "Content");
        articleList.AddPropertyType(new PropertyType(shortStringHelper, textArea, "intro") { Name = "Intro" }, "content", "Content");
        await seed.EnsureContentType(articleList.Key, () => articleList);

        var about = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.AboutType, Alias = "aboutPage", Name = "About Page", Icon = "icon-globe", Variations = ContentVariation.Culture,
        };
        about.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title", Variations = ContentVariation.Culture }, "content", "Content");
        about.AddPropertyType(new PropertyType(shortStringHelper, richText, "body") { Name = "Body", Variations = ContentVariation.Culture }, "content", "Content");
        await seed.EnsureContentType(about.Key, () => about);

        var home = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.HomeType, Alias = "home", Name = "Home", Icon = "icon-home", AllowedAsRoot = true,
            AllowedContentTypes =
            [
                new ContentTypeSort(ExampleContent.ArticleListType, 0, articleList.Alias),
                new ContentTypeSort(ExampleContent.AboutType, 1, about.Alias),
            ],
        };
        home.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title" }, "content", "Content");
        home.AddPropertyType(new PropertyType(shortStringHelper, textArea, "intro") { Name = "Intro" }, "content", "Content");
        home.AddPropertyType(new PropertyType(shortStringHelper, imagePicker, "heroImage") { Name = "Hero image" }, "content", "Content");
        home.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "featuredArticle") { Name = "Featured article" }, "content", "Content");
        home.AddPropertyType(new PropertyType(shortStringHelper, teasers, "teasers") { Name = "Teasers" }, "content", "Content");
        await seed.EnsureContentType(home.Key, () => home);

        seed.EnsureImage(ExampleContent.HarbourImage, "Harbour", new Rgba32(18, 72, 120), new Rgba32(120, 190, 220));
        seed.EnsureImage(ExampleContent.ForestImage, "Forest", new Rgba32(20, 70, 40), new Rgba32(150, 200, 110));
        seed.EnsureImage(ExampleContent.DuskImage, "Dusk", new Rgba32(90, 30, 90), new Rgba32(240, 150, 80));

        IContent homePage = seed.EnsurePage(ExampleContent.Home, "Home", null, "home", content =>
        {
            content.SetValue("title", "Cache Tags example");
            content.SetValue("intro", "A small site for checking that the frontend shows changes as soon as they are published.");
            content.SetValue("heroImage", Image(ExampleContent.DuskImage));
            content.SetValue("featuredArticle", Udi(ExampleContent.FirstArticle));
            content.SetValue("teasers", Teasers());
        });

        IContent listing = seed.EnsurePage(ExampleContent.Articles, "Articles", homePage, "articleList", content =>
        {
            content.SetValue("title", "Articles");
            content.SetValue("intro", "Everything we have written so far.");
        });

        seed.EnsurePage(ExampleContent.FirstArticle, "Harbour mornings", listing, "article", content =>
        {
            content.SetValue("title", "Harbour mornings");
            content.SetValue("summary", "What the harbour looks like before the city wakes up.");
            content.SetValue("image", Image(ExampleContent.HarbourImage));
            content.SetValue("body", RichText(
                "<p>The first ferry leaves at six. By then the fishermen have been out for hours.</p>"
                + $"<p>If you prefer trees to water, read <a href=\"/{{localLink:{ExampleContent.SecondArticle}}}\" type=\"document\" title=\"Forest walks\">Forest walks</a>.</p>"));
            content.SetValue("related", Udis(ExampleContent.SecondArticle, ExampleContent.ThirdArticle));
        });
        seed.EnsurePage(ExampleContent.SecondArticle, "Forest walks", listing, "article", content =>
        {
            content.SetValue("title", "Forest walks");
            content.SetValue("summary", "Three routes north of the city, all under two hours.");
            content.SetValue("image", Image(ExampleContent.ForestImage));
            content.SetValue("body", RichText("<p>Pick the short loop if it has rained; the long one floods by the lake.</p>"));
            content.SetValue("related", Udis(ExampleContent.FirstArticle));
        });
        seed.EnsurePage(ExampleContent.ThirdArticle, "Evenings at dusk", listing, "article", content =>
        {
            content.SetValue("title", "Evenings at dusk");
            content.SetValue("summary", "Where to sit when the sun goes down.");
            content.SetValue("image", Image(ExampleContent.DuskImage));
            content.SetValue("body", RichText(
                $"<p>Start at the harbour (see <a href=\"/{{localLink:{ExampleContent.FirstArticle}}}\" type=\"document\" title=\"Harbour mornings\">Harbour mornings</a>) and walk west.</p>"));
            content.SetValue("related", Udis(ExampleContent.FirstArticle, ExampleContent.SecondArticle));
        });

        IContent aboutPage = contentService.Create("About", homePage.Id, "aboutPage");
        aboutPage.Key = ExampleContent.About;
        aboutPage.SetCultureName("About", English);
        aboutPage.SetCultureName("Om os", Danish);
        aboutPage.SetValue("title", "About this site", English);
        aboutPage.SetValue("title", "Om dette site", Danish);
        aboutPage.SetValue("body", RichText("<p>This page exists in English and Danish. Publish one culture and only that one should change.</p>"), English);
        aboutPage.SetValue("body", RichText("<p>Denne side findes på engelsk og dansk. Udgiv én kultur, og kun den bør ændre sig.</p>"), Danish);
        seed.SaveAndPublish(aboutPage, "About");

        logger.LogInformation("Seeded the cache tags example content.");
    }

    private static string Teasers() => BlockList(
        Teaser(ExampleContent.ArticlesBlock, "Read the articles", "Short pieces about the city.", ExampleContent.Articles),
        Teaser(ExampleContent.AboutBlock, "About this site", "Available in English and Danish.", ExampleContent.About));

    private static Block Teaser(Guid key, string heading, string text, Guid link) => new(
        key,
        ExampleContent.TeaserBlockType,
        new Dictionary<string, object?> { ["heading"] = heading, ["text"] = text, ["link"] = Udi(link) });
}
