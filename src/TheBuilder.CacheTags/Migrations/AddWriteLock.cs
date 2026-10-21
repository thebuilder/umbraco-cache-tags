using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Infrastructure.Migrations;

namespace TheBuilder.CacheTags.Migrations;

/// <summary>The package's row in <c>umbracoLock</c>, which every package write locks first (see <see cref="PackageDatabase" />).</summary>
internal sealed class AddWriteLock(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (Database.ExecuteScalar<int>("SELECT COUNT(*) FROM umbracoLock WHERE id = @0", PackageDatabase.LockId) == 0)
        {
            Database.Execute("INSERT INTO umbracoLock (id, value, name) VALUES (@0, 1, @1)", PackageDatabase.LockId, Constants.PackageName);
        }

        return Task.CompletedTask;
    }
}
