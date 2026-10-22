namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>
///     Fixed keys for everything the seeders create, so cache keys (<c>content-&lt;guid&gt;</c>,
///     <c>media-&lt;guid&gt;</c>) are the same on every machine and can be written into the README.
///     <c>e0</c> documents, <c>e1</c> media, <c>e2</c> blocks, <c>e3</c> content types, <c>e4</c> the
///     API user, <c>e5</c> data types, <c>e6</c> block grid areas, and <c>e9</c> what
///     <c>samples/nextjs/scripts/e2e.mts</c> creates. Never change a key that has been seeded.
/// </summary>
public static class ExampleContent
{
    // Seeded by ExampleSeeder.
    public static readonly Guid Home = new("e0000000-0000-0000-0000-000000000001");
    public static readonly Guid Articles = new("e0000000-0000-0000-0000-000000000002");
    public static readonly Guid FirstArticle = new("e0000000-0000-0000-0000-000000000003");
    public static readonly Guid SecondArticle = new("e0000000-0000-0000-0000-000000000004");
    public static readonly Guid ThirdArticle = new("e0000000-0000-0000-0000-000000000005");
    public static readonly Guid About = new("e0000000-0000-0000-0000-000000000006");

    public static readonly Guid HarbourImage = new("e1000000-0000-0000-0000-000000000001");
    public static readonly Guid ForestImage = new("e1000000-0000-0000-0000-000000000002");
    public static readonly Guid DuskImage = new("e1000000-0000-0000-0000-000000000003");

    public static readonly Guid ArticlesBlock = new("e2000000-0000-0000-0000-000000000001");
    public static readonly Guid AboutBlock = new("e2000000-0000-0000-0000-000000000002");

    public static readonly Guid HomeType = new("e3000000-0000-0000-0000-000000000001");
    public static readonly Guid ArticleListType = new("e3000000-0000-0000-0000-000000000002");
    public static readonly Guid ArticleType = new("e3000000-0000-0000-0000-000000000003");
    public static readonly Guid AboutType = new("e3000000-0000-0000-0000-000000000004");
    public static readonly Guid TeaserBlockType = new("e3000000-0000-0000-0000-000000000005");

    // Seeded by ExampleSiteSeeder.

    // Guides: a section, two categories and guide pages, one of them a level deeper.
    public static readonly Guid Guides = new("e0000000-0000-0000-0000-000000000010");
    public static readonly Guid GettingAround = new("e0000000-0000-0000-0000-000000000011");
    public static readonly Guid ByBike = new("e0000000-0000-0000-0000-000000000012");
    public static readonly Guid BikeParking = new("e0000000-0000-0000-0000-000000000013");
    public static readonly Guid ByFerry = new("e0000000-0000-0000-0000-000000000014");
    public static readonly Guid OnFoot = new("e0000000-0000-0000-0000-000000000015");
    public static readonly Guid EatingOut = new("e0000000-0000-0000-0000-000000000016");
    public static readonly Guid Breakfast = new("e0000000-0000-0000-0000-000000000017");
    public static readonly Guid LateDinners = new("e0000000-0000-0000-0000-000000000018");

    /// <summary>Saved, never published, and picked from three places.</summary>
    public static readonly Guid StreetFood = new("e0000000-0000-0000-0000-000000000019");

    /// <summary>Linked only from On foot's rich text: no picker, listing or menu reaches it.</summary>
    public static readonly Guid HiddenCourtyard = new("e0000000-0000-0000-0000-000000000020");

    public static readonly Guid Blocks = new("e0000000-0000-0000-0000-000000000021");
    public static readonly Guid PhotoEssay = new("e0000000-0000-0000-0000-000000000022");
    public static readonly Guid Events = new("e0000000-0000-0000-0000-000000000023");
    public static readonly Guid Midsummer = new("e0000000-0000-0000-0000-000000000024");
    public static readonly Guid HarbourFestival = new("e0000000-0000-0000-0000-000000000025");

    /// <summary>Published in en-US only.</summary>
    public static readonly Guid WinterMarket = new("e0000000-0000-0000-0000-000000000026");

    public static readonly Guid PhotosFolder = new("e1000000-0000-0000-0000-000000000010");
    public static readonly Guid CityFolder = new("e1000000-0000-0000-0000-000000000011");
    public static readonly Guid NightFolder = new("e1000000-0000-0000-0000-000000000012");
    public static readonly Guid NatureFolder = new("e1000000-0000-0000-0000-000000000013");
    public static readonly Guid DocumentsFolder = new("e1000000-0000-0000-0000-000000000014");
    public static readonly Guid BridgeImage = new("e1000000-0000-0000-0000-000000000020");
    public static readonly Guid MarketImage = new("e1000000-0000-0000-0000-000000000021");
    public static readonly Guid RooftopsImage = new("e1000000-0000-0000-0000-000000000022");
    public static readonly Guid LanternsImage = new("e1000000-0000-0000-0000-000000000023");
    public static readonly Guid LakeImage = new("e1000000-0000-0000-0000-000000000024");
    public static readonly Guid MeadowImage = new("e1000000-0000-0000-0000-000000000025");

    /// <summary>Never picked: shown only inline in rich text (Hidden courtyard and Photo essay).</summary>
    public static readonly Guid CourtyardImage = new("e1000000-0000-0000-0000-000000000026");

    public static readonly Guid FerryTimetable = new("e1000000-0000-0000-0000-000000000030");

    public static readonly Guid CalloutBlockType = new("e3000000-0000-0000-0000-000000000006");
    public static readonly Guid HeroBlockType = new("e3000000-0000-0000-0000-000000000007");
    public static readonly Guid TextBlockType = new("e3000000-0000-0000-0000-000000000008");
    public static readonly Guid FeatureListBlockType = new("e3000000-0000-0000-0000-000000000009");
    public static readonly Guid RowBlockType = new("e3000000-0000-0000-0000-00000000000a");
    public static readonly Guid GalleryBlockType = new("e3000000-0000-0000-0000-00000000000b");
    public static readonly Guid SectionType = new("e3000000-0000-0000-0000-000000000010");
    public static readonly Guid CategoryType = new("e3000000-0000-0000-0000-000000000011");
    public static readonly Guid GuidePageType = new("e3000000-0000-0000-0000-000000000012");
    public static readonly Guid BlocksPageType = new("e3000000-0000-0000-0000-000000000013");
    public static readonly Guid PhotoEssayType = new("e3000000-0000-0000-0000-000000000014");
    public static readonly Guid EventListType = new("e3000000-0000-0000-0000-000000000015");
    public static readonly Guid EventType = new("e3000000-0000-0000-0000-000000000016");

    public static readonly Guid RichTextWithBlocksDataType = new("e5000000-0000-0000-0000-000000000001");
    public static readonly Guid TagsDataType = new("e5000000-0000-0000-0000-000000000002");
    public static readonly Guid DifficultyDataType = new("e5000000-0000-0000-0000-000000000003");
    public static readonly Guid LinksDataType = new("e5000000-0000-0000-0000-000000000004");
    public static readonly Guid DocumentPickerDataType = new("e5000000-0000-0000-0000-000000000005");
    public static readonly Guid GalleryDataType = new("e5000000-0000-0000-0000-000000000006");
    public static readonly Guid MediaPickerDataType = new("e5000000-0000-0000-0000-000000000007");
    public static readonly Guid FeaturesDataType = new("e5000000-0000-0000-0000-000000000008");
    public static readonly Guid SectionsDataType = new("e5000000-0000-0000-0000-000000000009");
    public static readonly Guid GridDataType = new("e5000000-0000-0000-0000-00000000000a");

    public static readonly Guid RowLeftArea = new("e6000000-0000-0000-0000-000000000001");
    public static readonly Guid RowRightArea = new("e6000000-0000-0000-0000-000000000002");
}
