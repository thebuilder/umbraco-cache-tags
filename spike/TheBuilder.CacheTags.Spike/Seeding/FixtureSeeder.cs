using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace TheBuilder.CacheTags.Spike.Seeding;

/// <summary>Deterministic keys for every fixture item, so the driver knows what to expect.</summary>
public static class Fixture
{
    public static readonly Guid Site = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Page = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid A = new("10000000-0000-0000-0000-00000000000a");
    public static readonly Guid B = new("10000000-0000-0000-0000-00000000000b");
    public static readonly Guid C = new("10000000-0000-0000-0000-00000000000c");
    public static readonly Guid D = new("10000000-0000-0000-0000-00000000000d");
    public static readonly Guid E = new("10000000-0000-0000-0000-00000000000e");
    public static readonly Guid F = new("10000000-0000-0000-0000-00000000000f");
    public static readonly Guid G = new("10000000-0000-0000-0000-000000000010");
    public static readonly Guid Mover = new("10000000-0000-0000-0000-000000000011");
    public static readonly Guid Variant = new("10000000-0000-0000-0000-000000000012");
    public static readonly Guid ChildOfC = new("10000000-0000-0000-0000-000000000013");
    public static readonly Guid M1 = new("20000000-0000-0000-0000-000000000001");
    public static readonly Guid M2 = new("20000000-0000-0000-0000-000000000002");
    public static readonly Guid M3 = new("20000000-0000-0000-0000-000000000003");
    public static readonly Guid M4 = new("20000000-0000-0000-0000-000000000004");
    public static readonly Guid Block = new("30000000-0000-0000-0000-000000000001");
    public static readonly Guid MediaPickerEntry = new("30000000-0000-0000-0000-000000000002");
    public static readonly Guid LinkBlockType = new("40000000-0000-0000-0000-000000000001");
    public static readonly Guid PageType = new("40000000-0000-0000-0000-000000000002");
    public static readonly Guid VariantType = new("40000000-0000-0000-0000-000000000003");

    public static object Describe() => new
    {
        site = Site, page = Page, a = A, b = B, c = C, d = D, e = E, f = F, g = G, mover = Mover, variant = Variant, childOfC = ChildOfC,
        m1 = M1, m2 = M2, m3 = M3, m4 = M4, block = Block,
        // What the fixture page renders: itself, every picked document and media item, links in rich text.
        expectedPage = new[] { "c:" + Page, "c:" + A, "c:" + B, "c:" + C, "c:" + D, "c:" + E, "c:" + F, "m:" + M1, "m:" + M2, "m:" + M3, "m:" + M4 },
        // Expanding the content picker also renders A's own picker value, G.
        expectedPageExpanded = new[] { "c:" + G },
    };
}

/// <summary>
///     Seeds the fixture on first start: data types for each picker kind, a block list, rich text,
///     a page type, a culture-variant type, four images and a small tree.
/// </summary>
internal sealed class FixtureSeeder(
    IRuntimeState runtimeState,
    IDataTypeService dataTypeService,
    IContentTypeService contentTypeService,
    IContentService contentService,
    IMediaService mediaService,
    ILanguageService languageService,
    PropertyEditorCollection propertyEditors,
    IConfigurationEditorJsonSerializer configurationSerializer,
    IShortStringHelper shortStringHelper,
    ILogger<FixtureSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly Guid User = Constants.Security.SuperUserKey;

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || contentTypeService.Get(Fixture.PageType) is not null)
        {
            return;
        }

        logger.LogInformation("Seeding the cache tags spike fixture.");

        if (await languageService.GetAsync("da-DK") is null)
        {
            await languageService.CreateAsync(new Language("da-DK", "Danish"), User);
        }

        IDataType contentPicker = await CreateDataType("Spike Content Picker", Constants.PropertyEditors.Aliases.ContentPicker, "Umb.PropertyEditorUi.DocumentPicker", ValueStorageType.Nvarchar);
        IDataType treePicker = await CreateDataType("Spike Tree Picker", Constants.PropertyEditors.Aliases.MultiNodeTreePicker, "Umb.PropertyEditorUi.ContentPicker", ValueStorageType.Ntext);
        IDataType urlPicker = await CreateDataType("Spike URL Picker", Constants.PropertyEditors.Aliases.MultiUrlPicker, "Umb.PropertyEditorUi.MultiUrlPicker", ValueStorageType.Ntext);
        IDataType mediaPicker = await CreateDataType("Spike Media Picker", Constants.PropertyEditors.Aliases.MediaPicker3, "Umb.PropertyEditorUi.MediaPicker", ValueStorageType.Ntext);
        IDataType richText = await CreateDataType("Spike Rich Text", Constants.PropertyEditors.Aliases.RichText, "Umb.PropertyEditorUi.Tiptap", ValueStorageType.Ntext);

        var linkBlock = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = Fixture.LinkBlockType, Alias = "spikeLinkBlock", Name = "Spike Link Block", IsElement = true,
        };
        linkBlock.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "target") { Name = "Target" }, "content", "Content");
        await contentTypeService.CreateAsync(linkBlock, User);

        IDataType blockList = await CreateDataType(
            "Spike Block List",
            Constants.PropertyEditors.Aliases.BlockList,
            "Umb.PropertyEditorUi.BlockList",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["blocks"] = new[] { new Dictionary<string, object> { ["contentElementTypeKey"] = Fixture.LinkBlockType } } });

        var page = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = Fixture.PageType, Alias = "spikePage", Name = "Spike Page", AllowedAsRoot = true,
        };
        page.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "picker") { Name = "Picker" }, "content", "Content");
        page.AddPropertyType(new PropertyType(shortStringHelper, treePicker, "tree") { Name = "Tree" }, "content", "Content");
        page.AddPropertyType(new PropertyType(shortStringHelper, urlPicker, "links") { Name = "Links" }, "content", "Content");
        page.AddPropertyType(new PropertyType(shortStringHelper, mediaPicker, "image") { Name = "Image" }, "content", "Content");
        page.AddPropertyType(new PropertyType(shortStringHelper, blockList, "blocks") { Name = "Blocks" }, "content", "Content");
        page.AddPropertyType(new PropertyType(shortStringHelper, richText, "body") { Name = "Body" }, "content", "Content");
        await contentTypeService.CreateAsync(page, User);

        var variant = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Key = Fixture.VariantType, Alias = "spikeVariant", Name = "Spike Variant", Variations = ContentVariation.Culture,
        };
        variant.AddPropertyType(new PropertyType(shortStringHelper, contentPicker, "picker") { Name = "Picker", Variations = ContentVariation.Culture }, "content", "Content");
        await contentTypeService.CreateAsync(variant, User);

        CreateImage(Fixture.M1, "Image 1");
        CreateImage(Fixture.M2, "Image 2");
        CreateImage(Fixture.M3, "Image 3");
        CreateImage(Fixture.M4, "Image 4");

        IContent site = CreatePage(Fixture.Site, "Site", null);
        IContent g = CreatePage(Fixture.G, "Target G", site);
        CreatePage(Fixture.A, "Target A", site, a => a.SetValue("picker", Udi(Constants.UdiEntityType.Document, g.Key)));
        CreatePage(Fixture.B, "Target B", site);
        IContent c = CreatePage(Fixture.C, "Target C", site);
        CreatePage(Fixture.D, "Target D", site);
        CreatePage(Fixture.E, "Target E", site);
        CreatePage(Fixture.F, "Target F", site);
        CreatePage(Fixture.Mover, "Mover", site);
        CreatePage(Fixture.Page, "Fixture", site, SetFixtureValues);

        IContent variantContent = contentService.Create("Variant", site.Id, "spikeVariant");
        variantContent.Key = Fixture.Variant;
        variantContent.SetCultureName("Variant", "en-US");
        variantContent.SetCultureName("Variant DA", "da-DK");
        variantContent.SetValue("picker", Udi(Constants.UdiEntityType.Document, Fixture.B), "en-US");
        variantContent.SetValue("picker", Udi(Constants.UdiEntityType.Document, Fixture.C), "da-DK");
        contentService.Save(variantContent);
        contentService.Publish(variantContent, ["*"]);

        _ = c;
        logger.LogInformation("Seeded the cache tags spike fixture.");
    }

    private static void SetFixtureValues(IContent content)
    {
        content.SetValue("picker", Udi(Constants.UdiEntityType.Document, Fixture.A));
        content.SetValue("tree", $"{Udi(Constants.UdiEntityType.Document, Fixture.B)},{Udi(Constants.UdiEntityType.Document, Fixture.C)}");
        content.SetValue("links", $$"""
            [
              { "name": "To D", "udi": "{{Udi(Constants.UdiEntityType.Document, Fixture.D)}}" },
              { "name": "To image 2", "udi": "{{Udi(Constants.UdiEntityType.Media, Fixture.M2)}}" }
            ]
            """);
        content.SetValue("image", $$"""
            [ { "key": "{{Fixture.MediaPickerEntry}}", "mediaKey": "{{Fixture.M1}}", "mediaTypeAlias": "Image", "crops": [], "focalPoint": null } ]
            """);
        content.SetValue("blocks", $$"""
            {
              "layout": { "Umbraco.BlockList": [ { "contentKey": "{{Fixture.Block}}" } ] },
              "contentData": [
                {
                  "contentTypeKey": "{{Fixture.LinkBlockType}}",
                  "key": "{{Fixture.Block}}",
                  "values": [ { "alias": "target", "culture": null, "segment": null, "value": "{{Udi(Constants.UdiEntityType.Document, Fixture.E)}}" } ]
                }
              ],
              "settingsData": [],
              "expose": [ { "contentKey": "{{Fixture.Block}}", "culture": null, "segment": null } ]
            }
            """);
        var markup = $"<p><a href=\"/{{localLink:{Fixture.F}}}\" type=\"document\" title=\"F\">F</a> "
            + $"<a href=\"/{{localLink:{Fixture.M3}}}\" type=\"media\" title=\"Image 3\">Image 3</a> "
            + $"<img src=\"/media/spike/4.png\" data-udi=\"{Udi(Constants.UdiEntityType.Media, Fixture.M4)}\" alt=\"\"></p>";
        content.SetValue("body", System.Text.Json.JsonSerializer.Serialize(new { markup, blocks = (object?)null }));
    }

    private static string Udi(string entityType, Guid key) => new GuidUdi(entityType, key).ToString();

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
        Attempt<IDataType, Umbraco.Cms.Core.Services.OperationStatus.DataTypeOperationStatus> attempt = await dataTypeService.CreateAsync(dataType, User);
        return attempt.Success ? attempt.Result : throw new InvalidOperationException($"Creating {name} failed: {attempt.Status}");
    }

    private void CreateImage(Guid key, string name)
    {
        IMedia media = mediaService.CreateMedia(name, Constants.System.Root, Constants.Conventions.MediaTypes.Image);
        media.Key = key;
        media.SetValue(Constants.Conventions.Media.File, $$"""{ "src": "/media/spike/{{name.Replace(' ', '-').ToLowerInvariant()}}.png", "crops": [] }""");
        mediaService.Save(media);
    }

    private IContent CreatePage(Guid key, string name, IContent? parent, Action<IContent>? setValues = null)
    {
        IContent content = contentService.Create(name, parent?.Id ?? Constants.System.Root, "spikePage");
        content.Key = key;
        setValues?.Invoke(content);
        contentService.Save(content);
        PublishResult result = contentService.Publish(content, ["*"]);
        if (result.Success is false)
        {
            throw new InvalidOperationException($"Publishing {name} failed: {result.Result}");
        }

        return content;
    }
}
