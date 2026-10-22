namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Thrown by the default store when some responses of a batch could not be recorded, naming them, so
///     the writer retries only those. Any other exception from a store means none of the batch is known
///     to be recorded.
/// </summary>
internal sealed class PartialRecordException(IReadOnlyCollection<string> responseKeys, IReadOnlyCollection<Exception> failures)
    : AggregateException($"Recording {responseKeys.Count} cache tag responses failed.", failures)
{
    /// <summary>The responses that were not recorded, tried or not.</summary>
    public IReadOnlyCollection<string> ResponseKeys { get; } = responseKeys;
}
