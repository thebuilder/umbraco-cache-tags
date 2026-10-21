using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     How the package reads and writes the Umbraco database. Every write takes the package's own
///     Umbraco write lock first: on SQLite, two transactions that each read and then write can
///     deadlock (both hold a read lock and wait to write), and Umbraco avoids that by locking before
///     it reads. On SQL Server it is a row lock that orders the package's own writes.
/// </summary>
internal sealed class PackageDatabase(IScopeProvider scopeProvider)
{
    /// <summary>The package's row in <c>umbracoLock</c>, added by its migration. Umbraco's own ids are -331 and up.</summary>
    public const int LockId = -91730;

    public T Read<T>(Func<IDatabase, T> read)
    {
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        return read(scope.Database);
    }

    public T Write<T>(Func<IDatabase, T> write)
    {
        using IScope scope = scopeProvider.CreateScope();
        scope.EagerWriteLock(LockId);
        T result = write(scope.Database);
        scope.Complete();
        return result;
    }

    public void Write(Action<IDatabase> write) => Write(database =>
    {
        write(database);
        return true;
    });
}
