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
        var names = new KnownNames(
            Fake<IContentTypeService>.Of((method, _) =>
            {
                if (method.Name != "GetAll")
                {
                    return null;
                }

                Interlocked.Increment(ref reads);
                return Volatile.Read(ref aliases).Select(alias => Fake<IContentType>.Of((m, _) => m.Name == "get_Alias" ? alias : null)).ToArray();
            }),
            Fake<IMediaTypeService>.Of((method, _) => method.Name == "GetAll" ? Array.Empty<IMediaType>() : null),
            Fake<ILanguageService>.Of((method, _) => method.Name == "GetAllAsync" ? Task.FromResult(Enumerable.Empty<ILanguage>()) : null),
            time,
            NullLogger<KnownNames>.Instance);

        Assert.True(names.IsType("article", media: false));
        Volatile.Write(ref aliases, ["page"]);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.All(Enumerable.Range(0, 20).AsParallel().Select(_ => names.IsType("article", media: false)).ToArray(), Assert.True);
        for (var tries = 0; names.IsType("page", media: false) is false; tries++)
        {
            Assert.True(tries < 200, "The names were not read again.");
            await Task.Delay(50);
        }

        Assert.Equal(2, reads);
    }
}
