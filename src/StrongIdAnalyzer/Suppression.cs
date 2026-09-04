// The "not your domain" predicate. Two .editorconfig lists feed it — namespaces and
// assembly names — and every place the analyzer asks "can the user tag this, and should
// its types count as domain types?" goes through IsSuppressed: the SIA002/SIA003 fix-site
// checks, the KnownTags walk, wrapper recognition, and the receiver-type walk that gives
// `child.Id` its covariant tags. One predicate keeps those sites agreeing about what a
// framework or SDK type is.
//
// Both lists use the same syntax: comma-separated, `.`-segmented, trailing `*` for a
// prefix match. Namespace patterns match the symbol's containing-namespace chain; assembly
// patterns match the containing assembly's simple name split on `.`, so `Microsoft.Graph*`
// covers `Microsoft.Graph` and `Microsoft.Graph.Core` without covering `Microsoft.Graphics`.
sealed class Suppression(ImmutableArray<NamePattern> namespaces, ImmutableArray<NamePattern> assemblies)
{
    const string namespacesKey = "strongidanalyzer.suppressed_namespaces";
    const string assembliesKey = "strongidanalyzer.suppressed_assemblies";

    // Library namespaces whose members we can't realistically tag. Noise for SIA002/SIA003
    // when a tagged id flows into BCL / framework APIs (e.g. logging, serialization,
    // dependency injection, Entity Framework). Users can override via .editorconfig.
    public static readonly ImmutableArray<NamePattern> DefaultNamespaces =
        [
            new(["System"], true),
            new(["Microsoft"], true)
        ];

    public static readonly Suppression Default = new(DefaultNamespaces, []);

    public ImmutableArray<NamePattern> Namespaces { get; } = namespaces;
    public ImmutableArray<NamePattern> Assemblies { get; } = assemblies;

    // Assembly verdicts are per assembly, not per symbol, so the name split happens once.
    readonly ConcurrentDictionary<IAssemblySymbol, bool> assemblyCache = new(SymbolEqualityComparer.Default);

    public static Suppression Read(
        AnalyzerConfigOptionsProvider options,
        Compilation compilation)
    {
        // Read from any syntax tree's options rather than GlobalOptions — `[*.cs]`
        // editorconfig entries are per-tree and never surface via GlobalOptions.
        // The value is project-uniform in practice, so a single tree sample is
        // sufficient and keeps this a one-time read at CompilationStart.
        var tree = compilation.SyntaxTrees.FirstOrDefault();
        if (tree is null)
        {
            return Default;
        }

        var treeOptions = options.GetOptions(tree);
        var namespaces = treeOptions.TryGetValue(namespacesKey, out var rawNamespaces)
            ? Parse(rawNamespaces)
            : DefaultNamespaces;
        var assemblies = treeOptions.TryGetValue(assembliesKey, out var rawAssemblies)
            ? Parse(rawAssemblies)
            : [];
        return new(namespaces, assemblies);
    }

    // Comma-separated list; an explicit empty value disables that list entirely.
    static ImmutableArray<NamePattern> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<NamePattern>();
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

    public bool IsSuppressed(ISymbol symbol) =>
        IsNamespaceSuppressed(symbol) ||
        IsAssemblySuppressed(symbol);

    // Matches the symbol's namespace against the pre-parsed patterns by walking the
    // namespace chain segment-wise — no ToDisplayString, no string concatenation.
    bool IsNamespaceSuppressed(ISymbol symbol)
    {
        if (Namespaces.IsEmpty)
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

        foreach (var pattern in Namespaces)
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

    bool IsAssemblySuppressed(ISymbol symbol)
    {
        if (Assemblies.IsEmpty)
        {
            return false;
        }

        var assembly = symbol.ContainingAssembly;
        if (assembly is null)
        {
            return false;
        }

        if (assemblyCache.TryGetValue(assembly, out var cached))
        {
            return cached;
        }

        var verdict = MatchesAssembly(assembly.Name.Split('.'));
        assemblyCache.TryAdd(assembly, verdict);
        return verdict;
    }

    // Same rules as the namespace walk, over an assembly name's `.` segments: a wildcard
    // pattern matches when its segments are a leading run of the name's, an exact pattern
    // when they are the whole name.
    bool MatchesAssembly(string[] nameSegments)
    {
        foreach (var pattern in Assemblies)
        {
            var segments = pattern.Segments;
            var segmentCount = segments.Length;

            if (segmentCount == 0)
            {
                if (pattern.IsWildcard)
                {
                    return true;
                }

                continue;
            }

            if (pattern.IsWildcard ? nameSegments.Length < segmentCount : nameSegments.Length != segmentCount)
            {
                continue;
            }

            var matched = true;
            for (var i = 0; i < segmentCount; i++)
            {
                if (nameSegments[i] != segments[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
