using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Migrations;

namespace TheBuilder.CacheTags.Tests;

/// <summary>The package's migration plan, run against SQLite as a fresh install and as an upgrade.</summary>
public sealed class MigrationTests
{
    private static readonly Guid Releasing = new("01a1606c-318c-77da-8780-ba1b54b6add1");
    private static readonly Guid TimedOut = new("01a1606c-318c-77da-8780-ba1b54b6add2");
    private static readonly Guid Recorded = new("01a1606c-318c-77da-8780-ba1b54b6add3");

    [Fact]
    public void A_fresh_install_reaches_the_final_state_with_the_lock_every_package_write_takes()
    {
        using MigratedDatabase migrated = MigratedDatabase.Create();

        Assert.Equal(migrated.Plan.FinalState, migrated.State);
        Assert.Equal("TheBuilder.CacheTags", migrated.Database.ExecuteScalar<string>("SELECT name FROM umbracoLock WHERE id = -91730"));
    }

    [Fact]
    public async Task Changes_recorded_before_the_ack_barrier_are_history_once_it_is_installed()
    {
        using MigratedDatabase migrated = MigratedDatabase.Before<AddDeliverySchema>();

        // The first step creates the table from today's row, so take the barrier's columns away
        // again to have the table it created before the barrier.
        foreach (var column in new[] { "Signal", "Status", "ReleasedUtc", "Missing" })
        {
            migrated.Database.Execute($"ALTER TABLE theBuilderCacheTagsEvent DROP COLUMN {column}");
        }

        InsertEvent(migrated, Recorded, status: null);

        await migrated.MigrateAsync();

        Assert.Equal("Released", Status(migrated, Recorded));
        Assert.Empty(LedgerSql.InStage(migrated.Database, ChangeStage.Pending, 10));
    }

    [Fact]
    public async Task Changes_mid_release_before_stages_are_released_again_and_timeouts_count_as_released()
    {
        using MigratedDatabase migrated = MigratedDatabase.Before<AddIndexAcks>();
        InsertEvent(migrated, Releasing, "Releasing");
        InsertEvent(migrated, TimedOut, "ReleasedAfterTimeout");

        await migrated.MigrateAsync();

        Assert.Equal([Releasing], LedgerSql.InStage(migrated.Database, ChangeStage.Pending, 10).Select(row => row.Id));
        Assert.Equal("Released", Status(migrated, TimedOut));
    }

    [Fact]
    public async Task An_ack_table_from_before_listings_waited_for_the_index_gets_the_column_that_records_it()
    {
        using MigratedDatabase migrated = MigratedDatabase.Before<AddIndexAcks>();

        // The ack barrier's step creates the table from today's row, so take the column away again
        // to have the table that step created before listings waited for the index.
        migrated.Database.Execute("ALTER TABLE theBuilderCacheTagsAck DROP COLUMN IndexedUtc");
        migrated.Database.Execute(
            "INSERT INTO theBuilderCacheTagsAck (EventId, Server, AckedUtc) VALUES (@0, 'server-1', @1)",
            Recorded, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));

        await migrated.MigrateAsync();

        AckSql.Indexed(migrated.Database, "server-1", [Recorded], new DateTime(2026, 10, 20, 9, 1, 0, DateTimeKind.Utc));
        Assert.Equal(["server-1"], AckSql.Acks(migrated.Database, [Recorded]).IndexedBy(Recorded));
    }

    private static void InsertEvent(MigratedDatabase migrated, Guid id, string? status)
    {
        // Only the columns every version of the table has, and Status when the test's version has it.
        if (status is null)
        {
            migrated.Database.Execute(
                "INSERT INTO theBuilderCacheTagsEvent (Id, CreatedUtc, Kind, Server, Change, Plan) VALUES (@0, @1, 'Published', 'backoffice', '{}', '{}')",
                id, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
        }
        else
        {
            migrated.Database.Execute(
                "INSERT INTO theBuilderCacheTagsEvent (Id, CreatedUtc, Kind, Server, Change, Plan, Status) VALUES (@0, @1, 'Published', 'backoffice', '{}', '{}', @2)",
                id, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), status);
        }
    }

    private static string Status(MigratedDatabase migrated, Guid id)
        => migrated.Database.ExecuteScalar<string>("SELECT Status FROM theBuilderCacheTagsEvent WHERE Id = @0", id);
}
