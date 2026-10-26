using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags;

/// <summary>
///     Records changes to output the package does not see change on its own: responses of a site's own
///     API (tagged with <see cref="CacheTaggedAttribute" />), or anything a person purges by hand. A
///     change goes the way the package's own changes go: into the ledger, past the ack barrier when it
///     waits for one, and out to every purge target.
/// </summary>
public interface ICacheTagsChanges
{
    /// <summary>
    ///     Records a change that purges <paramref name="keys" />: custom keys (<c>x-</c> and lowercase
    ///     letters, digits and hyphens), <c>content-</c> or <c>media-</c> keys of items, or
    ///     <c>umbraco</c> for everything.
    /// </summary>
    /// <param name="keys">The keys to purge.</param>
    /// <param name="waitFor">
    ///     A signal every server reports with <see cref="Applied" /> once it serves the change, such
    ///     as from the handler of the cache refresher that updates the data on each server. The purge
    ///     waits for every server's report (or the ack timeout). <c>null</c> purges at once, for output
    ///     read straight from the database.
    /// </param>
    /// <param name="reason">Why, for the log and the lookup API.</param>
    /// <returns>The change's id.</returns>
    /// <exception cref="ArgumentException">A key or the signal is not one a site may name.</exception>
    Guid Record(IEnumerable<string> keys, string? waitFor = null, string? reason = null);

    /// <summary>
    ///     Reports that this server serves the changes recorded with <paramref name="signal" />. Call it
    ///     on every server, from where the data is refreshed there.
    /// </summary>
    /// <exception cref="ArgumentException">The signal is not lowercase letters, digits, dots and hyphens.</exception>
    void Applied(string signal);
}

internal sealed class CacheTagsChanges(ChangeRecorder recorder, AckProcessor acks, TimeProvider time) : ICacheTagsChanges
{
    private const int MaxSignalLength = 100;

    public Guid Record(IEnumerable<string> keys, string? waitFor = null, string? reason = null)
    {
        var named = keys.ToList();
        if (named.Count == 0)
        {
            throw new ArgumentException("A change needs at least one key.", nameof(keys));
        }

        if (named.FirstOrDefault(key => CacheKeys.IsNameable(key) is false) is { } invalid)
        {
            throw new ArgumentException(
                $"\"{invalid}\" is not a key a site may name: use x- and lowercase letters, digits and hyphens (at most {CacheKeys.MaxCustomKeyLength} characters), an item's content- or media- key, or umbraco.",
                nameof(keys));
        }

        if (waitFor is not null)
        {
            ValidateSignal(waitFor, nameof(waitFor));
        }

        var change = new Change(
            Guid.CreateVersion7(),
            time.GetUtcNow().UtcDateTime,
            ChangeKind.Custom,
            [],
            [],
            null,
            reason,
            named,
            waitFor);
        recorder.Record(change);
        return change.Id;
    }

    public void Applied(string signal)
    {
        ValidateSignal(signal, nameof(signal));
        acks.Observe([new ObservedRefresh(AckSignal.CustomPrefix + signal, null)]);
    }

    /// <summary>Whether a signal name is lowercase letters, digits, dots and hyphens, and not too long.</summary>
    public static bool IsSignal(string signal)
        => signal.Length is > 0 and <= MaxSignalLength && signal.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.');

    private static void ValidateSignal(string signal, string parameter)
    {
        if (IsSignal(signal) is false)
        {
            throw new ArgumentException($"\"{signal}\" is not a signal: use lowercase letters, digits, dots and hyphens (at most {MaxSignalLength} characters).", parameter);
        }
    }
}
