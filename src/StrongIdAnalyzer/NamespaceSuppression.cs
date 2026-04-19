namespace StrongIdAnalyzer;

// Parsing and matching of the `strongidanalyzer.suppressed_namespaces` .editorconfig
// option. Patterns are parsed once into segment arrays so per-symbol matching avoids
// ToDisplayString and string concatenation.
static class NamespaceSuppression
{
    // .editorconfig key. Value is comma-separated; trailing `*` means prefix match
    // (e.g. `System*` matches `System`, `System.Collections`, etc.). Setting an
    // empty value disables suppression.
    const string optionKey = "strongidanalyzer.suppressed_namespaces";

    // Library namespaces whose members we can't realistically tag. Noise for SIA002/SIA003
    // when a tagged id flows into BCL / framework APIs (e.g. logging, serialization,
    // dependency injection, Entity Framework). Users can override via .editorconfig.
    public static readonly ImmutableArray<NamespacePattern> Default =
        [new(["System"], true), new(["Microsoft"], true)];

    public static ImmutableArray<NamespacePattern> Read(AnalyzerConfigOptionsProvider options)
    {
        if (!options.GlobalOptions.TryGetValue(optionKey, out var raw))
        {
            return Default;
        }

        // Explicit empty disables all suppression.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<NamespacePattern>();
        foreach (var entry in raw.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var isWildcard = trimmed[^1] == '*';
            var prefix = isWildcard ? trimmed[..^1] : trimmed;
            ImmutableArray<string> segments = prefix.Length == 0
                ? []
                : [..prefix.Split('.')];
            builder.Add(new(segments, isWildcard));
        }

        return builder.ToImmutable();
    }

    // Matches the symbol's namespace against the pre-parsed patterns by walking the
    // namespace chain segment-wise — no ToDisplayString, no string concatenation.
    public static bool IsSuppressed(ISymbol symbol, ImmutableArray<NamespacePattern> patterns)
    {
        if (patterns.IsEmpty)
        {
            return false;
        }

        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return false;
        }

        var depth = 0;
        for (var walker = ns; walker is { IsGlobalNamespace: false }; walker = walker.ContainingNamespace)
        {
            depth++;
        }

        foreach (var pattern in patterns)
        {
            var segments = pattern.Segments;
            var segmentCount = segments.Length;

            // Bare `*` — empty prefix with wildcard — matches any namespace.
            if (segmentCount == 0)
            {
                if (pattern.IsWildcard)
                {
                    return true;
                }

                continue;
            }

            if (pattern.IsWildcard ? depth < segmentCount : depth != segmentCount)
            {
                continue;
            }

            // Skip inner segments so `cursor` is the innermost segment of the pattern's
            // root-rooted prefix, then walk outward comparing segment-by-segment.
            var cursor = ns;
            for (var i = 0; i < depth - segmentCount; i++)
            {
                cursor = cursor!.ContainingNamespace;
            }

            var matched = true;
            for (var i = segmentCount - 1; i >= 0; i--)
            {
                if (cursor!.Name != segments[i])
                {
                    matched = false;
                    break;
                }

                cursor = cursor.ContainingNamespace;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}

readonly struct NamespacePattern(ImmutableArray<string> segments, bool isWildcard)
{
    public ImmutableArray<string> Segments { get; } = segments;
    public bool IsWildcard { get; } = isWildcard;
}
