// Compilation-wide type traversal. Used to resolve ancestor tag widening — we need
// every type in the source assembly *and* every referenced assembly that matches a
// given simple name, then walk its base chain / interface list.
static class TypeEnumeration
{
    // Simple name → the types that carry it, across the source assembly and every
    // referenced assembly. Compilation.GetSymbolsWithName only searches source
    // declarations, missing types defined in NuGet references or project dependencies,
    // so the walk is manual.
    //
    // Built once per compilation rather than per tag: the previous per-name search
    // enumerated every type in every referenced assembly on the first use of each tag,
    // which is the same work repeated once per domain in the codebase. Suppressed types
    // are dropped while building — widening is the only consumer, and it never wants
    // them — which also keeps the map to the user's own domain.
    public static Dictionary<string, ImmutableArray<INamedTypeSymbol>> BuildNameMap(
        Compilation compilation,
        Suppression suppression)
    {
        var builders = new Dictionary<string, ImmutableArray<INamedTypeSymbol>.Builder>(StringComparer.Ordinal);

        Add(compilation.Assembly.GlobalNamespace);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                Add(assembly.GlobalNamespace);
            }
        }

        var map = new Dictionary<string, ImmutableArray<INamedTypeSymbol>>(builders.Count, StringComparer.Ordinal);
        foreach (var entry in builders)
        {
            map.Add(entry.Key, entry.Value.ToImmutable());
        }

        return map;

        void Add(INamespaceSymbol ns)
        {
            foreach (var type in EnumerateAll(ns))
            {
                if (type.Name.Length == 0 ||
                    suppression.IsSuppressed(type))
                {
                    continue;
                }

                if (!builders.TryGetValue(type.Name, out var builder))
                {
                    builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
                    builders.Add(type.Name, builder);
                }

                builder.Add(type);
            }
        }
    }

    // Every type declared under `ns`, including nested types at any depth.
    public static IEnumerable<INamedTypeSymbol> EnumerateAll(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in EnumerateNested(type))
                    {
                        yield return nested;
                    }

                    break;
                case INamespaceSymbol child:
                    foreach (var type in EnumerateAll(child))
                    {
                        yield return type;
                    }

                    break;
            }
        }
    }

    public static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested))
            {
                yield return deeper;
            }
        }
    }
}
