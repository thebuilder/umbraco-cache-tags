namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>
///     Fixed keys for everything the seeder creates, so cache keys (<c>c-&lt;guid&gt;</c>,
///     <c>m-&lt;guid&gt;</c>) are the same on every machine and can be written into the README.
/// </summary>
public static class ExampleContent
{
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
}
