using System.Text.Json;
using SixLabors.ImageSharp.PixelFormats;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using static TheBuilder.CacheTags.Example.Seeding.SeedValues;

namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>
///     Seeds the rest of the site around what <see cref="ExampleSeeder" /> made: a Guides section with
///     nested categories and pages, a page of nested blocks, a media-heavy photo essay, culture-variant
///     events, and a media tree with folders, images and a PDF. Until Winter market (created last)
///     exists, every item is checked by key and only missing ones are created, so a database seeded
///     before this existed gets them too. See ../../README.md for what each item is there to test.
/// </summary>
internal sealed class ExampleSiteSeeder(
    IRuntimeState runtimeState,
    SeedWriter seed,
    IShortStringHelper shortStringHelper,
    ILogger<ExampleSiteSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string Danish = "da-DK";
    private const string English = "en-US";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run)
        {
            return;
        }

        IContent? home = seed.Get(ExampleContent.Home);
        if (home is null)
        {
            logger.LogWarning("The example Home page is missing, so the rest of the example site is not seeded.");
            return;
        }

        // Created last, so when it exists everything else does.
        if (seed.Get(ExampleContent.WinterMarket) is not null)
        {
            return;
        }

        logger.LogInformation("Seeding the rest of the cache tags example site.");
        await CreateTypes();
        IMedia courtyard = CreateMedia();
        CreateGuides(home, courtyard);
        CreateBlocksPage(home);
        CreatePhotoEssay(home, courtyard);
        CreateEvents(home);
        logger.LogInformation("Seeded the rest of the cache tags example site.");
    }

    private async Task CreateTypes()
    {
        IDataType text = await seed.BuiltInDataType(Constants.DataTypes.Guids.TextstringGuid);
        IDataType textArea = await seed.BuiltInDataType(Constants.DataTypes.Guids.TextareaGuid);
        IDataType date = await seed.BuiltInDataType(Constants.DataTypes.Guids.DatePickerGuid);

        // Created by ExampleSeeder without fixed keys, so found by name.
        IDataType contentPicker = await seed.ExistingDataType("Example Content Picker");
        IDataType imagePicker = await seed.ExistingDataType("Example Image Picker");
        IDataType richText = await seed.ExistingDataType("Example Rich Text");

        IDataType documentPicker = await seed.EnsureDataType(
            ExampleContent.DocumentPickerDataType, "Example Document Picker", Constants.PropertyEditors.Aliases.MultiNodeTreePicker, "Umb.PropertyEditorUi.ContentPicker", ValueStorageType.Ntext);
        IDataType mediaPicker = await seed.EnsureDataType(
            ExampleContent.MediaPickerDataType,
            "Example Media Picker",
            Constants.PropertyEditors.Aliases.MediaPicker3,
            "Umb.PropertyEditorUi.MediaPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["multiple"] = true });
        IDataType gallery = await seed.EnsureDataType(
            ExampleContent.GalleryDataType,
            "Example Gallery",
            Constants.PropertyEditors.Aliases.MediaPicker3,
            "Umb.PropertyEditorUi.MediaPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object>
            {
                ["multiple"] = true,
                ["enableLocalFocalPoint"] = true,
                ["filter"] = Constants.MediaTypes.Guids.Image,
                ["crops"] = new[]
                {
                    new Dictionary<string, object> { ["alias"] = "wide", ["label"] = "Wide", ["width"] = 1200, ["height"] = 500 },
                    new Dictionary<string, object> { ["alias"] = "square", ["label"] = "Square", ["width"] = 600, ["height"] = 600 },
                },
            });
        IDataType links = await seed.EnsureDataType(
            ExampleContent.LinksDataType, "Example Links", Constants.PropertyEditors.Aliases.MultiUrlPicker, "Umb.PropertyEditorUi.MultiUrlPicker", ValueStorageType.Ntext);
        IDataType tags = await seed.EnsureDataType(
            ExampleContent.TagsDataType,
            "Example Tags",
            Constants.PropertyEditors.Aliases.Tags,
            "Umb.PropertyEditorUi.Tags",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["group"] = "default", ["storageType"] = "Json" });
        IDataType difficulty = await seed.EnsureDataType(
            ExampleContent.DifficultyDataType,
            "Example Difficulty",
            Constants.PropertyEditors.Aliases.DropDownListFlexible,
            "Umb.PropertyEditorUi.Dropdown",
            ValueStorageType.Nvarchar,
            new Dictionary<string, object> { ["items"] = new[] { "Easy", "Moderate", "Hard" }, ["multiple"] = false });

        await seed.EnsureContentType(ExampleContent.CalloutBlockType, () => Element("calloutBlock", "Callout", "icon-lightbulb", (text, "heading", "Heading"), (textArea, "text", "Text"), (contentPicker, "link", "Link")));

        // The built-in rich text configuration (toolbar and extensions, for the backoffice), plus a
        // callout block.
        IDataType builtInRichText = await seed.BuiltInDataType(Constants.DataTypes.Guids.RichtextEditorGuid);
        IDataType richTextWithBlocks = await seed.EnsureDataType(
            ExampleContent.RichTextWithBlocksDataType,
            "Example Rich Text With Blocks",
            Constants.PropertyEditors.Aliases.RichText,
            "Umb.PropertyEditorUi.Tiptap",
            ValueStorageType.Ntext,
            new Dictionary<string, object>(builtInRichText.ConfigurationData)
            {
                ["blocks"] = new[] { new Dictionary<string, object> { ["contentElementTypeKey"] = ExampleContent.CalloutBlockType, ["label"] = "{umbValue: heading}" } },
            });

        await seed.EnsureContentType(ExampleContent.HeroBlockType, () => Element("heroBlock", "Hero", "icon-picture", (text, "heading", "Heading"), (imagePicker, "image", "Image"), (links, "links", "Links")));
        await seed.EnsureContentType(ExampleContent.TextBlockType, () => Element("textBlock", "Text", "icon-text", (richText, "body", "Body")));
        await seed.EnsureContentType(ExampleContent.GalleryBlockType, () => Element("galleryBlock", "Gallery", "icon-pictures", (text, "heading", "Heading"), (gallery, "images", "Images")));
        await seed.EnsureContentType(ExampleContent.RowBlockType, () => Element("rowBlock", "Row", "icon-layout", (text, "heading", "Heading")));

        // A block list inside a block: the feature list's items are teasers.
        IDataType features = await seed.EnsureDataType(
            ExampleContent.FeaturesDataType,
            "Example Features",
            Constants.PropertyEditors.Aliases.BlockList,
            "Umb.PropertyEditorUi.BlockList",
            ValueStorageType.Ntext,
            new Dictionary<string, object> { ["blocks"] = BlockConfig(ExampleContent.TeaserBlockType) });
        await seed.EnsureContentType(ExampleContent.FeatureListBlockType, () => Element("featureListBlock", "Feature List", "icon-bulleted-list", (text, "heading", "Heading"), (features, "items", "Items")));

        IDataType sections = await seed.EnsureDataType(
            ExampleContent.SectionsDataType,
            "Example Sections",
            Constants.PropertyEditors.Aliases.BlockList,
            "Umb.PropertyEditorUi.BlockList",
            ValueStorageType.Ntext,
            new Dictionary<string, object>
            {
                ["blocks"] = BlockConfig(ExampleContent.HeroBlockType, ExampleContent.FeatureListBlockType, ExampleContent.CalloutBlockType, ExampleContent.GalleryBlockType),
            });

        Dictionary<string, object> GridBlock(Guid elementType, params (Guid Key, string Alias)[] areas) => new()
        {
            ["contentElementTypeKey"] = elementType,
            ["allowAtRoot"] = true,
            ["allowInAreas"] = true,
            ["areaGridColumns"] = 12,
            ["areas"] = areas.Select(area => new Dictionary<string, object> { ["key"] = area.Key, ["alias"] = area.Alias, ["columnSpan"] = 6, ["rowSpan"] = 1 }).ToArray(),
        };
        IDataType grid = await seed.EnsureDataType(
            ExampleContent.GridDataType,
            "Example Grid",
            Constants.PropertyEditors.Aliases.BlockGrid,
            "Umb.PropertyEditorUi.BlockGrid",
            ValueStorageType.Ntext,
            new Dictionary<string, object>
            {
                ["gridColumns"] = 12,
                ["blocks"] = new[]
                {
                    GridBlock(ExampleContent.RowBlockType, (ExampleContent.RowLeftArea, "left"), (ExampleContent.RowRightArea, "right")),
                    GridBlock(ExampleContent.HeroBlockType),
                    GridBlock(ExampleContent.TextBlockType),
                    GridBlock(ExampleContent.CalloutBlockType),
                    GridBlock(ExampleContent.GalleryBlockType),
                },
            });

        await seed.EnsureContentType(ExampleContent.GuidePageType, () => Document(
            "guidePage",
            "Guide Page",
            "icon-map",
            (text, "title", "Title"),
            (textArea, "summary", "Summary"),
            (imagePicker, "image", "Image"),
            (richTextWithBlocks, "body", "Body"),
            (difficulty, "difficulty", "Difficulty"),
            (tags, "tags", "Tags"),
            (documentPicker, "related", "Related"),
            (mediaPicker, "attachments", "Attachments"),
            (links, "links", "Links")));
        await seed.EnsureContentType(ExampleContent.CategoryType, () => Document(
            "category", "Category", "icon-folder", (text, "title", "Title"), (textArea, "intro", "Intro"), (documentPicker, "featured", "Featured")));
        await seed.EnsureContentType(ExampleContent.SectionType, () => Document(
            "section", "Section", "icon-book-alt", (text, "title", "Title"), (textArea, "intro", "Intro"), (imagePicker, "image", "Image"), (documentPicker, "featured", "Featured")));
        await seed.EnsureContentType(ExampleContent.BlocksPageType, () => Document(
            "blocksPage", "Blocks Page", "icon-layout", (text, "title", "Title"), (textArea, "intro", "Intro"), (grid, "grid", "Grid"), (sections, "sections", "Sections"), (links, "links", "Links")));
        await seed.EnsureContentType(ExampleContent.PhotoEssayType, () => Document(
            "photoEssay", "Photo Essay", "icon-pictures-alt-2", (text, "title", "Title"), (textArea, "intro", "Intro"), (gallery, "gallery", "Gallery"), (mediaPicker, "downloads", "Downloads"), (richText, "body", "Body")));
        await seed.EnsureContentType(ExampleContent.EventListType, () => Document(
            "eventList", "Event List", "icon-calendar", (text, "title", "Title"), (textArea, "intro", "Intro")));
        await seed.EnsureContentType(ExampleContent.EventType, () =>
        {
            // Name, title and description per culture; date, image and venue shared by both.
            ContentType type = Document("event", "Event", "icon-calendar-alt", (date, "date", "Date"), (imagePicker, "image", "Image"), (contentPicker, "venue", "Venue"));
            type.Variations = ContentVariation.Culture;
            type.AddPropertyType(new PropertyType(shortStringHelper, text, "title") { Name = "Title", Variations = ContentVariation.Culture }, "content", "Content");
            type.AddPropertyType(new PropertyType(shortStringHelper, textArea, "description") { Name = "Description", Variations = ContentVariation.Culture }, "content", "Content");
            return type;
        });

        await seed.EnsureAllowedChildren(
            ExampleContent.HomeType, ExampleContent.SectionType, ExampleContent.BlocksPageType, ExampleContent.PhotoEssayType, ExampleContent.EventListType, ExampleContent.GuidePageType);
        await seed.EnsureAllowedChildren(ExampleContent.SectionType, ExampleContent.CategoryType);
        await seed.EnsureAllowedChildren(ExampleContent.CategoryType, ExampleContent.GuidePageType);
        await seed.EnsureAllowedChildren(ExampleContent.GuidePageType, ExampleContent.GuidePageType);
        await seed.EnsureAllowedChildren(ExampleContent.EventListType, ExampleContent.EventType);
    }

    private static Dictionary<string, object>[] BlockConfig(params Guid[] elementTypes) =>
        elementTypes.Select(key => new Dictionary<string, object> { ["contentElementTypeKey"] = key }).ToArray();

    private ContentType Element(string alias, string name, string icon, params (IDataType DataType, string Alias, string Name)[] properties)
    {
        ContentType type = Document(alias, name, icon, properties);
        type.IsElement = true;
        return type;
    }

    private ContentType Document(string alias, string name, string icon, params (IDataType DataType, string Alias, string Name)[] properties)
    {
        var type = new ContentType(shortStringHelper, Constants.System.Root) { Alias = alias, Name = name, Icon = icon };
        foreach ((IDataType dataType, string propertyAlias, string propertyName) in properties)
        {
            type.AddPropertyType(new PropertyType(shortStringHelper, dataType, propertyAlias) { Name = propertyName }, "content", "Content");
        }

        return type;
    }

    /// <summary>Creates the media tree, and returns the image only rich text shows.</summary>
    private IMedia CreateMedia()
    {
        IMedia photos = seed.EnsureFolder(ExampleContent.PhotosFolder, "Photos");
        IMedia city = seed.EnsureFolder(ExampleContent.CityFolder, "City", photos);
        IMedia night = seed.EnsureFolder(ExampleContent.NightFolder, "Night", city);
        IMedia nature = seed.EnsureFolder(ExampleContent.NatureFolder, "Nature", photos);
        IMedia documents = seed.EnsureFolder(ExampleContent.DocumentsFolder, "Documents");

        seed.EnsureImage(ExampleContent.BridgeImage, "Bridge", new Rgba32(40, 40, 60), new Rgba32(200, 120, 60), city);
        seed.EnsureImage(ExampleContent.MarketImage, "Market", new Rgba32(160, 40, 40), new Rgba32(250, 210, 90), city);
        seed.EnsureImage(ExampleContent.RooftopsImage, "Rooftops", new Rgba32(110, 60, 50), new Rgba32(180, 200, 230), city);
        seed.EnsureImage(ExampleContent.LanternsImage, "Lanterns", new Rgba32(10, 10, 40), new Rgba32(250, 180, 40), night);
        seed.EnsureImage(ExampleContent.LakeImage, "Lake", new Rgba32(10, 50, 90), new Rgba32(170, 220, 240), nature);
        seed.EnsureImage(ExampleContent.MeadowImage, "Meadow", new Rgba32(60, 110, 30), new Rgba32(240, 230, 120), nature);
        IMedia courtyard = seed.EnsureImage(ExampleContent.CourtyardImage, "Courtyard", new Rgba32(70, 90, 70), new Rgba32(220, 200, 170), photos);

        seed.EnsurePdf(
            ExampleContent.FerryTimetable,
            "Ferry timetable",
            "ferry-timetable.pdf",
            documents,
            "Ferry timetable",
            "Weekdays: every 20 minutes from 06:00 to 23:40.",
            "Weekends: every 30 minutes from 07:00 to 00:30.");

        return courtyard;
    }

    private void CreateGuides(IContent home, IMedia courtyard)
    {
        IContent guides = seed.EnsurePage(ExampleContent.Guides, "Guides", home, "section", content =>
        {
            content.SetValue("title", "Guides");
            content.SetValue("intro", "Practical guides to the city, by topic.");
            content.SetValue("image", Image(ExampleContent.BridgeImage));
            content.SetValue("featured", Udis(ExampleContent.ByFerry, ExampleContent.Breakfast));
        });

        IContent gettingAround = seed.EnsurePage(ExampleContent.GettingAround, "Getting around", guides, "category", content =>
        {
            content.SetValue("title", "Getting around");
            content.SetValue("intro", "Bikes, ferries and your own two feet.");
            content.SetValue("featured", Udis(ExampleContent.ByFerry));
        });

        IContent byBike = seed.EnsurePage(ExampleContent.ByBike, "By bike", gettingAround, "guidePage", content => Guide(
            content,
            "By bike",
            "The city is flat and the bike lanes are wide.",
            ExampleContent.RooftopsImage,
            $"<p>Most streets have a lane of their own. Where to leave the bike is in <a href=\"/{{localLink:{ExampleContent.BikeParking}}}\" type=\"document\" title=\"Bike parking\">Bike parking</a>.</p>",
            "Easy",
            ["bike", "city"],
            related: [ExampleContent.OnFoot],
            links: [Link.External("City bike map", "https://example.com/bike-map")]));

        // Five levels from the root: renaming Guides or Getting around changes this route too.
        seed.EnsurePage(ExampleContent.BikeParking, "Bike parking", byBike, "guidePage", content => Guide(
            content,
            "Bike parking",
            "Racks by every station, and a guarded garage under the main square.",
            null,
            "<p>The garage under the square is free for the first two hours.</p>",
            "Easy",
            ["bike"],
            related: [ExampleContent.ByFerry]));

        Block timetableCallout = new(BlockKey(0x10), ExampleContent.CalloutBlockType, new Dictionary<string, object?>
        {
            ["heading"] = "Breakfast on the other side",
            ["text"] = "Take the first ferry and eat by the water.",
            ["link"] = Udi(ExampleContent.Breakfast),
        });
        seed.EnsurePage(ExampleContent.ByFerry, "By ferry", gettingAround, "guidePage", content => Guide(
            content,
            "By ferry",
            "Harbour buses cross every twenty minutes.",
            ExampleContent.BridgeImage,
            RichText(
                $"<p>The timetable is in <a href=\"/{{localLink:{ExampleContent.FerryTimetable}}}\" type=\"media\" title=\"Ferry timetable\">the ferry timetable (PDF)</a>.</p>"
                + RichTextBlock(timetableCallout)
                + "<p>Bikes ride free outside rush hour.</p>",
                timetableCallout),
            "Easy",
            ["harbour", "ferry"],
            related: [ExampleContent.Breakfast, ExampleContent.FirstArticle],
            attachments: [(ExampleContent.FerryTimetable, Constants.Conventions.MediaTypes.File), (ExampleContent.HarbourImage, Constants.Conventions.MediaTypes.Image)],
            links:
            [
                Link.Document("Harbour festival", ExampleContent.HarbourFestival),
                Link.Media("Ferry timetable", ExampleContent.FerryTimetable),
                Link.External("Ferry operator", "https://example.com/ferries"),
            ]));

        seed.EnsurePage(ExampleContent.OnFoot, "On foot", gettingAround, "guidePage", content => Guide(
            content,
            "On foot",
            "The old town is best seen walking.",
            ExampleContent.MeadowImage,
            "<p>Leave the main street at the fountain and keep left.</p>"
            + $"<p>At the end is <a href=\"/{{localLink:{ExampleContent.HiddenCourtyard}}}\" type=\"document\" title=\"Hidden courtyard\">a courtyard</a> nothing else links to.</p>",
            "Moderate",
            ["walking"],
            related: [ExampleContent.ByBike]));

        IContent eatingOut = seed.EnsurePage(ExampleContent.EatingOut, "Eating out", guides, "category", content =>
        {
            content.SetValue("title", "Eating out");
            content.SetValue("intro", "Where to eat, from the first coffee to the last plate.");

            // Street food is never published, so the Delivery API leaves it out until it is.
            content.SetValue("featured", Udis(ExampleContent.StreetFood, ExampleContent.LateDinners));
        });

        seed.EnsurePage(ExampleContent.Breakfast, "Breakfast by the water", eatingOut, "guidePage", content => Guide(
            content,
            "Breakfast by the water",
            "Three cafés on the quay that open at seven.",
            ExampleContent.HarbourImage,
            "<p>Sit outside if you can; the sun reaches the quay by eight.</p>",
            "Easy",
            ["harbour", "food"],
            related: [ExampleContent.ByFerry]));

        seed.EnsurePage(ExampleContent.LateDinners, "Late dinners", eatingOut, "guidePage", content => Guide(
            content,
            "Late dinners",
            "Kitchens that stay open past ten.",
            ExampleContent.LanternsImage,
            "<p>Book ahead on Fridays.</p>",
            "Moderate",
            ["food", "night"],
            related: [ExampleContent.StreetFood, ExampleContent.Breakfast]));

        seed.EnsureDocument(ExampleContent.StreetFood, "Street food", eatingOut, "guidePage", content => Guide(
            content,
            "Street food",
            "Not written yet.",
            ExampleContent.MarketImage,
            "<p>Coming soon.</p>",
            "Easy",
            ["food"]), publish: []);

        // Under Home, where no listing looks: only On foot's rich text links here.
        seed.EnsurePage(ExampleContent.HiddenCourtyard, "Hidden courtyard", home, "guidePage", content => Guide(
            content,
            "Hidden courtyard",
            "A quiet square behind the old town hall.",
            null,
            "<p>Through the gate, past the bins, and there it is.</p>"
            + $"<p><img src=\"{FileUrl(courtyard)}\" alt=\"Courtyard\" width=\"600\" height=\"338\" data-udi=\"{MediaUdi(ExampleContent.CourtyardImage)}\"></p>",
            "Hard",
            ["walking", "secret"]));
    }

    private static void Guide(
        IContent content,
        string title,
        string summary,
        Guid? image,
        string body,
        string difficulty,
        string[] tags,
        Guid[]? related = null,
        (Guid Key, string MediaTypeAlias)[]? attachments = null,
        Link[]? links = null)
    {
        content.SetValue("title", title);
        content.SetValue("summary", summary);
        if (image is { } imageKey)
        {
            content.SetValue("image", Image(imageKey));
        }

        // Plain markup is wrapped; RichText(markup, blocks) output is already a stored value.
        content.SetValue("body", body.StartsWith('{') ? body : RichText(body));
        content.SetValue("difficulty", Strings(difficulty));
        content.SetValue("tags", Strings(tags));
        if (related is not null)
        {
            content.SetValue("related", Udis(related));
        }

        if (attachments is not null)
        {
            content.SetValue("attachments", Media(attachments.Select(pick => (pick.Key, pick.MediaTypeAlias, Array.Empty<Crop>())).ToArray()));
        }

        if (links is not null)
        {
            content.SetValue("links", Links(links));
        }
    }

    private void CreateBlocksPage(IContent home)
    {
        var n = 0x20;
        Block Next(Guid type, Dictionary<string, object?> values) => new(BlockKey(n++), type, values);
        Block Callout(string heading, string text, Guid link) => Next(ExampleContent.CalloutBlockType, new() { ["heading"] = heading, ["text"] = text, ["link"] = Udi(link) });
        Block Teaser(string heading, string text, Guid link) => Next(ExampleContent.TeaserBlockType, new() { ["heading"] = heading, ["text"] = text, ["link"] = Udi(link) });

        Block hero = Next(ExampleContent.HeroBlockType, new()
        {
            ["heading"] = "Over the bridge",
            ["image"] = Image(ExampleContent.BridgeImage),
            ["links"] = Links(Link.Document("Cross by ferry instead", ExampleContent.ByFerry), Link.External("Bridge webcam", "https://example.com/bridge-cam")),
        });
        Block text = Next(ExampleContent.TextBlockType, new()
        {
            ["body"] = RichText($"<p>A text block in a grid area, linking to <a href=\"/{{localLink:{ExampleContent.Breakfast}}}\" type=\"document\" title=\"Breakfast by the water\">breakfast</a>.</p>"),
        });
        Block ferryCallout = Callout("Ferries", "Every twenty minutes, day and night.", ExampleContent.ByFerry);
        Block firstRow = Next(ExampleContent.RowBlockType, new() { ["heading"] = "Two columns" });
        Block gallery = Next(ExampleContent.GalleryBlockType, new()
        {
            ["heading"] = "City at night",
            ["images"] = Media(
                (ExampleContent.MarketImage, Constants.Conventions.MediaTypes.Image, [new Crop("wide", 1200, 500, 0, 0.1, 0, 0.3)]),
                (ExampleContent.LanternsImage, Constants.Conventions.MediaTypes.Image, [new Crop("square", 600, 600, 0.2, 0, 0.2, 0)])),
        });
        Block guidesCallout = Callout("All guides", "Every guide, by topic.", ExampleContent.Guides);
        Block innerRow = Next(ExampleContent.RowBlockType, new() { ["heading"] = "A row inside a row" });
        Block innerText = Next(ExampleContent.TextBlockType, new() { ["body"] = RichText("<p>Two levels of areas deep.</p>") });
        Block eventsCallout = Callout("Events", "What is on this year.", ExampleContent.Events);
        Block secondRow = Next(ExampleContent.RowBlockType, new() { ["heading"] = "Nested rows" });

        Block sectionsHero = Next(ExampleContent.HeroBlockType, new()
        {
            ["heading"] = "Blocks all the way down",
            ["image"] = Image(ExampleContent.MeadowImage),
            ["links"] = Links(Link.Document("Events", ExampleContent.Events)),
        });
        Block features = Next(ExampleContent.FeatureListBlockType, new()
        {
            ["heading"] = "A block list inside a block",
            ["items"] = BlockListValue(
                Teaser("Guides", "Start here.", ExampleContent.Guides),
                Teaser("Street food", "Unpublished, so this link is empty until it is published.", ExampleContent.StreetFood),
                Teaser("This page", "A nested block that links back to the page it is on.", ExampleContent.Blocks)),
        });
        Block selfCallout = Callout("This page", "A callout that links back to the page it is on.", ExampleContent.Blocks);

        seed.EnsurePage(ExampleContent.Blocks, "Blocks", home, "blocksPage", content =>
        {
            content.SetValue("title", "Blocks");
            content.SetValue("intro", "A block grid with areas, a block list with a block list inside, and links of every kind.");
            // A block in an area spans the area's columns (6), not the grid's (12).
            content.SetValue("grid", BlockGrid(
                new GridItem(firstRow, 12, new Dictionary<Guid, GridItem[]>
                {
                    [ExampleContent.RowLeftArea] = [new GridItem(hero, 6)],
                    [ExampleContent.RowRightArea] = [new GridItem(text, 6), new GridItem(ferryCallout, 6)],
                }),
                new GridItem(gallery, 12),
                new GridItem(secondRow, 12, new Dictionary<Guid, GridItem[]>
                {
                    [ExampleContent.RowLeftArea] = [new GridItem(guidesCallout, 6)],
                    [ExampleContent.RowRightArea] =
                    [
                        new GridItem(innerRow, 6, new Dictionary<Guid, GridItem[]>
                        {
                            [ExampleContent.RowLeftArea] = [new GridItem(innerText, 6)],
                            [ExampleContent.RowRightArea] = [new GridItem(eventsCallout, 6)],
                        }),
                    ],
                })));
            content.SetValue("sections", BlockList(sectionsHero, features, selfCallout));
            content.SetValue("links", Links(
                Link.Document("Guides", ExampleContent.Guides),
                Link.Document("By ferry, with a query string", ExampleContent.ByFerry, "?from=blocks"),
                Link.Media("Ferry timetable (PDF)", ExampleContent.FerryTimetable),
                Link.External("Umbraco documentation", "https://docs.umbraco.com/")));
        });
    }

    private void CreatePhotoEssay(IContent home, IMedia courtyard)
    {
        // Fractions cut from each edge: a 1200x675 image keeps 1200x500 (wide) or 675x675 (square).
        Crop Wide(double top) => new("wide", 1200, 500, 0, top, 0, Math.Round(0.26 - top, 2));
        Crop Square(double left) => new("square", 600, 600, left, 0, Math.Round(0.44 - left, 2), 0);

        seed.EnsurePage(ExampleContent.PhotoEssay, "Photo essay", home, "photoEssay", content =>
        {
            content.SetValue("title", "Photo essay");
            content.SetValue("intro", "One day in the city, in seven pictures and a timetable.");
            content.SetValue("gallery", Media(
                (ExampleContent.BridgeImage, Constants.Conventions.MediaTypes.Image, [Wide(0.1), Square(0.2)]),
                (ExampleContent.MarketImage, Constants.Conventions.MediaTypes.Image, [Wide(0.13)]),
                (ExampleContent.RooftopsImage, Constants.Conventions.MediaTypes.Image, []),
                (ExampleContent.LanternsImage, Constants.Conventions.MediaTypes.Image, [Square(0.1)]),
                (ExampleContent.LakeImage, Constants.Conventions.MediaTypes.Image, [Wide(0)]),
                (ExampleContent.MeadowImage, Constants.Conventions.MediaTypes.Image, []),
                (ExampleContent.DuskImage, Constants.Conventions.MediaTypes.Image, [])));
            content.SetValue("downloads", Media((ExampleContent.FerryTimetable, Constants.Conventions.MediaTypes.File, [])));
            content.SetValue("body", RichText(
                $"<p>It starts on the water (<a href=\"/{{localLink:{ExampleContent.ByFerry}}}\" type=\"document\" title=\"By ferry\">by ferry</a>, timetable <a href=\"/{{localLink:{ExampleContent.FerryTimetable}}}\" type=\"media\" title=\"Ferry timetable\">here</a>) and ends in a courtyard:</p>"
                + $"<p><img src=\"{FileUrl(courtyard)}\" alt=\"Courtyard\" width=\"600\" height=\"338\" data-udi=\"{MediaUdi(ExampleContent.CourtyardImage)}\"></p>"));
        });
    }

    /// <summary>An image's URL, for an <c>img</c> in rich text. The Delivery API resolves it again from <c>data-udi</c>.</summary>
    private static string FileUrl(IMedia image) =>
        image.GetValue<string>(Constants.Conventions.Media.File) is { } file
            ? JsonDocument.Parse(file).RootElement.GetProperty("src").GetString() ?? string.Empty
            : string.Empty;

    private void CreateEvents(IContent home)
    {
        IContent events = seed.EnsurePage(ExampleContent.Events, "Events", home, "eventList", content =>
        {
            content.SetValue("title", "Events");
            content.SetValue("intro", "Each event is in English and Danish, except the winter market.");
        });

        void Event(Guid key, DateTime date, Guid image, Guid venue, (string Name, string Description) english, (string Name, string Description)? danish)
        {
            string[] cultures = danish is null ? [English] : [English, Danish];
            seed.EnsureDocument(key, english.Name, events, "event", content =>
            {
                content.SetValue("date", date);
                content.SetValue("image", Image(image));
                content.SetValue("venue", Udi(venue));
                content.SetCultureName(english.Name, English);
                content.SetValue("title", english.Name, English);
                content.SetValue("description", english.Description, English);
                if (danish is { } da)
                {
                    content.SetCultureName(da.Name, Danish);
                    content.SetValue("title", da.Name, Danish);
                    content.SetValue("description", da.Description, Danish);
                }
            }, publish: cultures);
        }

        Event(
            ExampleContent.Midsummer,
            new DateTime(2027, 6, 23, 20, 0, 0),
            ExampleContent.BridgeImage,
            ExampleContent.Breakfast,
            ("Midsummer night", "Bonfires along the shore."),
            ("Sankthans", "Bål langs stranden."));
        Event(
            ExampleContent.HarbourFestival,
            new DateTime(2027, 8, 14, 12, 0, 0),
            ExampleContent.HarbourImage,
            ExampleContent.ByFerry,
            ("Harbour festival", "Boats, music and food on the quay."),
            ("Havnefest", "Både, musik og mad på kajen."));
        Event(
            ExampleContent.WinterMarket,
            new DateTime(2026, 12, 5, 10, 0, 0),
            ExampleContent.MarketImage,
            ExampleContent.LateDinners,
            ("Winter market", "Stalls in the square every Saturday in December."),
            null);
    }
}
