using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class BackoffTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(9, 512)]
    [InlineData(10, 600)]
    [InlineData(25, 600)]
    public void Failed_purges_back_off_doubling_up_to_ten_minutes(int failedAttempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Backoff.After(failedAttempts));
    }
}
