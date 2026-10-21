using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Core.Strings;

namespace TheBuilder.CacheTags.Example.Seeding;

public sealed class ExampleSeederComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) =>
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ExampleSeeder>();
}

/// <summary>
///     Seeds a small site on first start: a home page with a block list, an article listing with three
///     articles that pick each other and an image each, and an about page in en-US and da-DK.
/// </summary>
internal sealed class ExampleSeeder(
    IRuntimeState runtimeState,
    IDataTypeService dataTypeService,
    IContentTypeService contentTypeService,
    IContentService contentService,
    IMediaService mediaService,
    ILanguageService languageService,
    MediaFileManager mediaFileManager,
    PropertyEditorCollection propertyEditors,
    IConfigurationEditorJsonSerializer configurationSerializer,
    IShortStringHelper shortStringHelper,
    ILogger<ExampleSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string Danish = "da-DK";
    private const string English = "en-US";
    private static readonly Guid User = Constants.Security.SuperUserKey;

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

        IDataType text = await dataTypeService.GetAsync(Constants.DataTypes.Guids.TextstringGuid)
            ?? throw new InvalidOperationException("The built-in Textstring data type is missing.");
        IDataType textArea = await dataTypeService.GetAsync(Constants.DataTypes.Guids.TextareaGuid)
            ?? throw new InvalidOperationException("The built-in Textarea data type is missing.");
        IDataType contentPicker = await CreateDataType("Example Content Picker", Constants.PropertyEditors.Aliases.ContentPicker, "Umb.PropertyEditorUi.DocumentPicker", ValueStorageType.Nvarchar);
        IDataType articlePicker = await CreateDataType(
            "Example Article Picker",
            Constants.PropertyEditors.Aliases.MultiNodeTreePicker,
            "Umb.PropertyEditorUi.ContentPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["filter"] = ExampleContent.ArticleType.ToString() });
        IDataType imagePicker = await CreateDataType(
            "Example Image Picker",
            Constants.PropertyEditors.Aliases.MediaPicker3,
            "Umb.PropertyEditorUi.MediaPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["multiple"] = false });
        IDataType richText = await CreateDataType("Example Rich Text", Constants.PropertyEditors.Aliases.RichText, "Umb.PropertyEditorUi.Tiptap", ValueStorageType.Ntext);

        var teaser = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.TeaserBlockType, Alias = "teaserBlock", Name = "Teaser", Icon = "icon-navigation-right", IsElement = true,
        };
        teaser.AddPropertyType(new PropertyType(shortStringHelper, text, "heading") { Name = "Heading" }, "content", "Content");
        teaser.AddPropertyType(new PropertyType(shortStringHelper, textArea, "text") { Name = "Text" }, "content", "Content");
        teaser.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "link") { Name = "Link" }, "content", "Content");
        await CreateContentType(teaser);

        IDataType teasers = await CreateDataType(
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
        await CreateContentType(article);

        var articleList = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.ArticleListType, Alias = "articleList", Name = "Article List", Icon = "icon-documents",
            AllowedContentTypes = [new ContentTypeSort(ExampleContent.ArticleType, 0, article.Alias)],
        };
        articleList.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title" }, "content", "Content");
        articleList.AddPropertyType(new PropertyType(shortStringHelper, textArea, "intro") { Name = "Intro" }, "content", "Content");
        await CreateContentType(articleList);

        var about = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = ExampleContent.AboutType, Alias = "aboutPage", Name = "About Page", Icon = "icon-globe", Variations = ContentVariation.Culture,
        };
        about.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title", Variations = ContentVariation.Culture }, "content", "Content");
        about.AddPropertyType(new PropertyType(shortStringHelper, richText, "body") { Name = "Body", Variations = ContentVariation.Culture }, "content", "Content");
        await CreateContentType(about);

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
        await CreateContentType(home);

        CreateImage(ExampleContent.HarbourImage, "Harbour", new Rgba32(18, 72, 120), new Rgba32(120, 190, 220));
        CreateImage(ExampleContent.ForestImage, "Forest", new Rgba32(20, 70, 40), new Rgba32(150, 200, 110));
        CreateImage(ExampleContent.DuskImage, "Dusk", new Rgba32(90, 30, 90), new Rgba32(240, 150, 80));

        IContent homePage = CreatePage(ExampleContent.Home, "Home", null, "home", content =>
        {
            content.SetValue("title", "Cache Tags example");
            content.SetValue("intro", "A small site for checking that the frontend shows changes as soon as they are published.");
            content.SetValue("heroImage", Image(ExampleContent.DuskImage));
            content.SetValue("featuredArticle", Udi(ExampleContent.FirstArticle));
            content.SetValue("teasers", Teasers());
        });

        IContent listing = CreatePage(ExampleContent.Articles, "Articles", homePage, "articleList", content =>
        {
            content.SetValue("title", "Articles");
            content.SetValue("intro", "Everything we have written so far.");
        });

        CreatePage(ExampleContent.FirstArticle, "Harbour mornings", listing, "article", content =>
        {
            content.SetValue("title", "Harbour mornings");
            content.SetValue("summary", "What the harbour looks like before the city wakes up.");
            content.SetValue("image", Image(ExampleContent.HarbourImage));
            content.SetValue("body", RichText(
                "<p>The first ferry leaves at six. By then the fishermen have been out for hours.</p>"
                + $"<p>If you prefer trees to water, read <a href=\"/{{localLink:{ExampleContent.SecondArticle}}}\" type=\"document\" title=\"Forest walks\">Forest walks</a>.</p>"));
            content.SetValue("related", Udis(ExampleContent.SecondArticle, ExampleContent.ThirdArticle));
        });
        CreatePage(ExampleContent.SecondArticle, "Forest walks", listing, "article", content =>
        {
            content.SetValue("title", "Forest walks");
            content.SetValue("summary", "Three routes north of the city, all under two hours.");
            content.SetValue("image", Image(ExampleContent.ForestImage));
            content.SetValue("body", RichText("<p>Pick the short loop if it has rained; the long one floods by the lake.</p>"));
            content.SetValue("related", Udis(ExampleContent.FirstArticle));
        });
        CreatePage(ExampleContent.ThirdArticle, "Evenings at dusk", listing, "article", content =>
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
        SaveAndPublish(aboutPage, "About");

        logger.LogInformation("Seeded the cache tags example content.");
    }

    private static string Udi(Guid key) => new GuidUdi(Constants.UdiEntityType.Document, key).ToString();

    private static string Udis(params Guid[] keys) => string.Join(',', keys.Select(Udi));

    private static string Image(Guid mediaKey) => JsonSerializer.Serialize(new[]
    {
        new { key = Guid.NewGuid(), mediaKey, mediaTypeAlias = Constants.Conventions.MediaTypes.Image, crops = Array.Empty<object>(), focalPoint = (object?)null },
    });

    private static string RichText(string markup) => JsonSerializer.Serialize(new { markup, blocks = (object?)null });

    private static string Teasers()
    {
        object Teaser(Guid key, string heading, string text, Guid link) => new
        {
            contentTypeKey = ExampleContent.TeaserBlockType,
            key,
            values = new object[]
            {
                new { alias = "heading", culture = (string?)null, segment = (string?)null, value = heading },
                new { alias = "text", culture = (string?)null, segment = (string?)null, value = text },
                new { alias = "link", culture = (string?)null, segment = (string?)null, value = Udi(link) },
            },
        };

        Guid[] keys = [ExampleContent.ArticlesBlock, ExampleContent.AboutBlock];
        return JsonSerializer.Serialize(new
        {
            layout = new Dictionary<string, object> { ["Umbraco.BlockList"] = keys.Select(key => new { contentKey = key }) },
            contentData = new[]
            {
                Teaser(ExampleContent.ArticlesBlock, "Read the articles", "Short pieces about the city.", ExampleContent.Articles),
                Teaser(ExampleContent.AboutBlock, "About this site", "Available in English and Danish.", ExampleContent.About),
            },
            settingsData = Array.Empty<object>(),
            expose = keys.Select(key => new { contentKey = key, culture = (string?)null, segment = (string?)null }),
        });
    }

    private async Task<IDataType> CreateDataType(string name, string editorAlias, string editorUiAlias, ValueStorageType storage, IDictionary<string, object>? configuration = null)
    {
        IDataEditor editor = propertyEditors[editorAlias] ?? throw new InvalidOperationException($"No property editor {editorAlias}.");
        var dataType = new DataType(editor, configurationSerializer)
        {
            Name = name,
            EditorUiAlias = editorUiAlias,
            DatabaseType = storage,
            ConfigurationData = configuration ?? new Dictionary<string, object>(),
        };
        Attempt<IDataType, DataTypeOperationStatus> attempt = await dataTypeService.CreateAsync(dataType, User);
        return attempt.Success ? attempt.Result : throw new InvalidOperationException($"Creating the {name} data type failed: {attempt.Status}");
    }

    private async Task CreateContentType(IContentType contentType)
    {
        Attempt<ContentTypeOperationStatus> attempt = await contentTypeService.CreateAsync(contentType, User);
        if (attempt.Success is false)
        {
            throw new InvalidOperationException($"Creating the {contentType.Alias} content type failed: {attempt.Result}");
        }
    }

    // A gradient PNG, so the frontend has a real file to show without binaries in the repository.
    private void CreateImage(Guid key, string name, Rgba32 from, Rgba32 to)
    {
        IMedia media = mediaService.CreateMedia(name, Constants.System.Root, Constants.Conventions.MediaTypes.Image);
        media.Key = key;

        const int width = 1200;
        const int height = 675;
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var t = (x + y) / (float)(width + height);
                    row[x] = new Rgba32(
                        (byte)(from.R + ((to.R - from.R) * t)),
                        (byte)(from.G + ((to.G - from.G) * t)),
                        (byte)(from.B + ((to.B - from.B) * t)));
                }
            }
        });

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;

        IPropertyType fileProperty = media.Properties[Constants.Conventions.Media.File]?.PropertyType
            ?? throw new InvalidOperationException("The Image media type has no umbracoFile property.");
        var fileName = $"{name.ToLowerInvariant()}.png";
        var path = mediaFileManager.StoreFile(media, fileProperty, fileName, stream, null);
        var url = mediaFileManager.FileSystem.GetUrl(path);

        media.SetValue(Constants.Conventions.Media.File, JsonSerializer.Serialize(new { src = url, crops = Array.Empty<object>() }));
        media.SetValue(Constants.Conventions.Media.Width, width);
        media.SetValue(Constants.Conventions.Media.Height, height);
        media.SetValue(Constants.Conventions.Media.Bytes, stream.Length);
        media.SetValue(Constants.Conventions.Media.Extension, "png");
        mediaService.Save(media);
    }

    private IContent CreatePage(Guid key, string name, IContent? parent, string contentTypeAlias, Action<IContent> setValues)
    {
        IContent content = contentService.Create(name, parent?.Id ?? Constants.System.Root, contentTypeAlias);
        content.Key = key;
        setValues(content);
        SaveAndPublish(content, name);
        return content;
    }

    private void SaveAndPublish(IContent content, string name)
    {
        contentService.Save(content);
        PublishResult result = contentService.Publish(content, ["*"]);
        if (result.Success is false)
        {
            throw new InvalidOperationException($"Publishing {name} failed: {result.Result}");
        }
    }
}
