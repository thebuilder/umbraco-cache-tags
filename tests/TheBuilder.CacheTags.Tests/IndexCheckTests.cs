using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

public sealed class IndexCheckTests
{
    private const string Now = "12|11|da-DK,en-US|-1,1060,1061|2";
    private const string Before = "10|9|da-DK,en-US|-1,1060,1061|2";

    [Fact]
    public void An_item_is_indexed_once_every_culture_was_built_from_its_state_now()
    {
        Assert.False(IndexCheck.HasIndexed(Now, [new IndexedDocument("en-us", Now), new IndexedDocument("da-dk", Before)]));
        Assert.True(IndexCheck.HasIndexed(Now, [new IndexedDocument("en-us", Now), new IndexedDocument("da-dk", Now)]));
    }

    [Fact]
    public void A_document_built_before_the_package_was_installed_has_not_caught_up()
    {
        Assert.False(IndexCheck.HasIndexed(Now, [new IndexedDocument("none", null)]));
    }

    [Fact]
    public void A_new_item_waits_until_the_index_has_it()
    {
        Assert.False(IndexCheck.HasIndexed(Now, []));
    }

    [Fact]
    public void An_item_that_should_have_no_documents_is_indexed_once_none_is_left()
    {
        Assert.False(IndexCheck.HasIndexed(null, [new IndexedDocument("none", Before)]));
        Assert.True(IndexCheck.HasIndexed(null, []));
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Published), true)]
    [InlineData(nameof(ChangeKind.Sorted), true)]
    [InlineData(nameof(ChangeKind.Moved), true)]
    [InlineData(nameof(ChangeKind.MediaSaved), false)]
    [InlineData(nameof(ChangeKind.TypesChanged), false)]
    [InlineData(nameof(ChangeKind.Everything), false)]
    public void Only_content_item_changes_wait_for_the_index(string kind, bool waits)
    {
        Assert.Equal(waits, IndexCheck.Waits(Enum.Parse<ChangeKind>(kind)));
    }
}
