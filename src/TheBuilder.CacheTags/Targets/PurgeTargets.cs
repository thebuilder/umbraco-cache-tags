using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Targets;

/// <summary>
///     Every target purges go to: those in configuration (<c>CacheTags:Targets</c>) and those added in
///     the backoffice. A configured target wins over a backoffice one of the same name. The backoffice
///     targets are read at most every few seconds, so another server sees a change within that.
/// </summary>
internal sealed class PurgeTargets(
    IOptionsMonitor<CacheTagsOptions> options,
    TargetStore store,
    PackageDatabase database,
    IEnumerable<IPurgeTarget> senders,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<PurgeTargets> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _warnedClashes = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset ReadAt, IReadOnlyList<TargetDefinition> Targets)? _stored;

    /// <summary>The registered target types.</summary>
    public IReadOnlyList<string> Types => senders.Select(sender => sender.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every target, by name.</summary>
    public IReadOnlyDictionary<string, ResolvedTarget> Current()
    {
        CacheTagsOptions settings = options.CurrentValue;
        return Merge(
            settings.Targets,
            Stored(),
            stored => TargetResolver.Resolve(stored, Types, configuration, settings.AllowPrivateNetworkTargets),
            WarnOnce);
    }

    /// <summary>
    ///     The configured targets, and the backoffice ones whose names configuration does not use.
    ///     <paramref name="clash" /> is told each backoffice target that is ignored.
    /// </summary>
    public static Dictionary<string, ResolvedTarget> Merge(
        IReadOnlyDictionary<string, CacheTagsTargetOptions> configured,
        IEnumerable<TargetDefinition> stored,
        Func<TargetDefinition, ResolvedTarget> resolve,
        Action<string> clash)
    {
        var targets = configured.ToDictionary(
            target => target.Key,
            target => new ResolvedTarget(target.Key, target.Value, FromConfiguration: true),
            StringComparer.OrdinalIgnoreCase);
        foreach (TargetDefinition target in stored)
        {
            if (targets.ContainsKey(target.Name))
            {
                clash(target.Name);
                continue;
            }

            targets[target.Name] = resolve(target);
        }

        return targets;
    }

    private void WarnOnce(string name)
    {
        lock (_warnedClashes)
        {
            if (_warnedClashes.Add(name))
            {
                logger.LogWarning("The backoffice cache tags target {Target} is ignored: CacheTags:Targets configures a target of the same name.", name);
            }
        }
    }

    /// <summary>Forgets the targets read, after this server changed them.</summary>
    public void Invalidate() => _stored = null;

    private IReadOnlyList<TargetDefinition> Stored()
    {
        if (database.IsInstalled() is false)
        {
            return [];
        }

        DateTimeOffset now = time.GetUtcNow();
        if (_stored is { } stored && now - stored.ReadAt < CacheFor)
        {
            return stored.Targets;
        }

        IReadOnlyList<TargetDefinition> targets = store.All();
        _stored = (now, targets);
        return targets;
    }
}
