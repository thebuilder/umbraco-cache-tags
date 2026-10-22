using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Tests;

public sealed class UmbracoCacheTagStoreTests
{
    [Fact]
    public async Task A_response_that_cannot_be_recorded_fails_the_call_so_the_writer_tries_it_again()
    {
        // Every statement fails, so the response fails its own retry as well.
        IUmbracoDatabase database = Fake<IUmbracoDatabase>.Of((_, _) => throw new InvalidOperationException("The database is busy."));
        IScope scope = Fake<IScope>.Of((method, _) => method.Name == "get_Database" ? database : null);
        var store = new UmbracoCacheTagStore(
            new PackageDatabase(
                Fake<IScopeProvider>.Of((method, _) => method.Name == "CreateScope" ? scope : null),
                Fake<IRuntimeState>.Of((method, _) => method.Name == "get_Level" ? RuntimeLevel.Run : null),
                Fake<IKeyValueService>.Of((method, _) => method.Name == "GetValue" ? new CacheTagsMigrationPlan().FinalState : null)),
            NullLogger<UmbracoCacheTagStore>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordAsync([Writers.Response("response-1")]));
    }
}
