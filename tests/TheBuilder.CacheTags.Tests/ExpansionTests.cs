using TheBuilder.CacheTags.Collection;

namespace TheBuilder.CacheTags.Tests;

public sealed class ExpansionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void No_or_unknown_expand_expands_nothing(string? expand)
    {
        Assert.True(Expansion.Parse(expand).IsNone);
    }

    [Theory]
    [InlineData("properties[$all]")]
    [InlineData("all")]
    public void All_expands_every_property(string expand)
    {
        Expansion expansion = Expansion.Parse(expand);

        Assert.True(expansion.Expands("anything"));
    }

    [Fact]
    public void V2_lists_the_top_level_aliases_and_ignores_nested_expansion()
    {
        Expansion expansion = Expansion.Parse("properties[picker,related[properties[image]],tree]");

        Assert.Equal(["picker", "related", "tree"], expansion.Properties.Order(StringComparer.Ordinal));
        Assert.False(expansion.Expands("image"));
    }

    [Fact]
    public void V1_lists_the_aliases_after_property()
    {
        Assert.Equal(["a", "b"], Expansion.Parse("property:a,b").Properties.Order(StringComparer.Ordinal));
    }
}
