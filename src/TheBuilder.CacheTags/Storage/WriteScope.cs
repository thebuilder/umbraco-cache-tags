using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Every write the package makes takes its own Umbraco write lock first. On SQLite, a transaction
///     that reads and then writes can deadlock with another doing the same (both hold a read lock and
///     wait to write); Umbraco avoids that by locking before it reads, and so must the package. On
///     SQL Server it is a row lock that orders the package's own writes.
/// </summary>
internal static class WriteScope
{
    /// <summary>The package's row in <c>umbracoLock</c>, added by its migration. Umbraco's own ids are -331 and up.</summary>
    public const int LockId = -91730;

    public static IScope Create(IScopeProvider scopeProvider)
    {
        IScope scope = scopeProvider.CreateScope();
        try
        {
            scope.EagerWriteLock(LockId);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        return scope;
    }
}
