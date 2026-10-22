namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Which properties of the requested items a Delivery API request expands. An expanded picker
///     renders the picked item's own properties, so the items those reference are rendered too.
/// </summary>
/// <param name="All">Every property is expanded.</param>
/// <param name="Properties">The expanded property aliases, when not all.</param>
internal sealed record Expansion(bool All, IReadOnlySet<string> Properties)
{
    public static readonly Expansion None = new(false, new HashSet<string>());

    public bool Expands(string alias) => All || Properties.Contains(alias);

    public bool IsNone => All is false && Properties.Count == 0;

    /// <summary>
    ///     Reads the top level of an <c>expand</c> parameter: v2 <c>properties[$all]</c> or
    ///     <c>properties[a,b[properties[c]]]</c>, and v1 <c>all</c> or <c>property:a,b</c>. Nested
    ///     expansion below the top level is not followed.
    /// </summary>
    public static Expansion Parse(string? expand)
    {
        if (string.IsNullOrWhiteSpace(expand))
        {
            return None;
        }

        expand = expand.Trim();
        if (expand.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new Expansion(true, new HashSet<string>());
        }

        if (expand.StartsWith("property:", StringComparison.OrdinalIgnoreCase))
        {
            return new Expansion(false, Aliases(expand["property:".Length..].Split(',')));
        }

        const string prefix = "properties[";
        if (expand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) is false || expand.EndsWith(']') is false)
        {
            return None;
        }

        var inner = expand[prefix.Length..^1];
        var aliases = TopLevel(inner).ToList();
        return aliases.Contains("$all")
            ? new Expansion(true, new HashSet<string>())
            : new Expansion(false, Aliases(aliases));
    }

    // Splits "a,b[properties[c,d]],e" on the commas outside brackets, dropping each nested part.
    private static IEnumerable<string> TopLevel(string inner)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            var c = i < inner.Length ? inner[i] : ',';
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                var part = inner[start..i];
                var bracket = part.IndexOf('[', StringComparison.Ordinal);
                yield return (bracket >= 0 ? part[..bracket] : part).Trim();
                start = i + 1;
            }
        }
    }

    private static HashSet<string> Aliases(IEnumerable<string> aliases)
        => aliases.Select(a => a.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
