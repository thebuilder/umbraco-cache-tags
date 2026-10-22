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
    public async Task Responses_that_cannot_be_recorded_fail_the_call_naming_them_so_the_writer_tries_them_again()
    {
        var statements = 0;
        UmbracoCacheTagStore store = Store(() => Interlocked.Increment(ref statements) is not (3 or 4));

        PartialRecordException failure = await Assert.ThrowsAsync<PartialRecordException>(
            () => store.RecordAsync([Writers.Response("response-1"), Writers.Response("response-2")]));

        // The first response fails twice; the second is written on its first try.
        Assert.Equal(["response-1"], failure.ResponseKeys);
        Assert.All(failure.InnerExceptions, exception => Assert.Equal("The database is busy.", exception.Message));
    }

    [Fact]
    public async Task After_two_responses_in_a_row_fail_the_rest_are_not_tried()
    {
        var statements = 0;
        UmbracoCacheTagStore store = Store(() =>
        {
            Interlocked.Increment(ref statements);
            return true;
        });

        PartialRecordException failure = await Assert.ThrowsAsync<PartialRecordException>(
            () => store.RecordAsync([Writers.Response("response-1"), Writers.Response("response-2"), Writers.Response("response-3")]));

        Assert.Equal(["response-1", "response-2", "response-3"], failure.ResponseKeys);
        Assert.Equal(4, statements);
    }

    /// <summary>A store on a database whose first statement in each write fails when <paramref name="fails" /> says so.</summary>
    private static UmbracoCacheTagStore Store(Func<bool> fails)
    {
        // CacheTagSql.Record starts with an UPDATE; past it, every read finds nothing and every write
        // succeeds.
        IUmbracoDatabase database = Fake.Of<IUmbracoDatabase>((method, _) => method.Name switch
        {
            "Execute" when fails() => throw new InvalidOperationException("The database is busy."),
            "Fetch" => Activator.CreateInstance(method.ReturnType),
            _ => null,
        });
        IScope scope = Fake.Of<IScope>((method, _) => method.Name == "get_Database" ? database : null);
        return new UmbracoCacheTagStore(
            new PackageDatabase(
                Fake.Of<IScopeProvider>((method, _) => method.Name == "CreateScope" ? scope : null),
                Fake.Of<IRuntimeState>((method, _) => method.Name == "get_Level" ? RuntimeLevel.Run : null),
                Fake.Of<IKeyValueService>((method, _) => method.Name == "GetValue" ? new CacheTagsMigrationPlan().FinalState : null)),
            NullLogger<UmbracoCacheTagStore>.Instance);
    }
}
