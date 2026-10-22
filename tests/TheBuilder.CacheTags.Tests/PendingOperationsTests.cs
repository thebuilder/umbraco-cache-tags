using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

public sealed class PendingOperationsTests
{
    private static readonly Guid Item = new("10000000-0000-0000-0000-00000000000a");

    [Fact]
    public void Segments_kept_before_a_publish_are_read_back_once()
    {
        using var pending = new PendingOperations();
        pending.RememberSegments(Item, new Dictionary<string, string?> { ["en-US"] = "harbour" });

        Assert.Equal("harbour", pending.TakeSegments(Item)!["en-US"]);
        Assert.Null(pending.TakeSegments(Item));
    }

    [Fact]
    public void A_sort_mark_swallows_one_publish_of_the_item()
    {
        using var pending = new PendingOperations();
        pending.MarkSorting([Item]);

        Assert.True(pending.TakeSorting(Item));
        Assert.False(pending.TakeSorting(Item));
        Assert.False(pending.TakeSorting(Guid.NewGuid()));
    }
}
