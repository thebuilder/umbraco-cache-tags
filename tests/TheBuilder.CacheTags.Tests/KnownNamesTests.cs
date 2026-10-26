using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Collection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Tests;

public sealed class KnownNamesTests
{
    [Fact]
    public async Task Once_the_names_are_a_minute_old_one_request_reads_them_again_while_the_others_use_the_old_ones()
    {
        var time = new ManualTime();
        var reads = 0;
        var aliases = new[] { "article" };

        // Every read after the first waits until the test lets it finish.
        var release = new ManualResetEventSlim();
        var names = new KnownNames(
            Fake.Of<IContentTypeService>((method, _) =>
            {
                if (method.Name != "GetAll")
                {
                    return null;
                }

                if (Interlocked.Increment(ref reads) > 1)
                {
                    release.Wait(TimeSpan.FromSeconds(10));
                }

                return Volatile.Read(ref aliases).Select(alias => Fake.Of<IContentType>((m, _) => m.Name == "get_Alias" ? alias : null)).ToArray();
            }),
            Fake.Of<IMediaTypeService>((method, _) => method.Name == "GetAll" ? Array.Empty<IMediaType>() : null),
            time,
            NullLogger<KnownNames>.Instance);

        Assert.True(names.IsType("article", media: false));
        Volatile.Write(ref aliases, ["page"]);
        time.Advance(TimeSpan.FromMinutes(2));

        // While the read is held, every request answers at once, from the old names.
        var answers = await Task.Run(() => Enumerable.Range(0, 20).Select(_ => names.IsType("article", media: false)).ToArray())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(answers, Assert.True);

        release.Set();
        for (var tries = 0; names.IsType("page", media: false) is false; tries++)
        {
            Assert.True(tries < 200, "The names were not read again.");
            await Task.Delay(50);
        }

        Assert.Equal(2, reads);
    }
}
