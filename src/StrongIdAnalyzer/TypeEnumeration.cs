// Compilation-wide type traversal. Used to resolve ancestor tag widening — we need
// every type in the source assembly *and* every referenced assembly that matches a
// given simple name, then walk its base chain / interface list.
static class TypeEnumeration
{
    // Finds every named type whose simple name equals `name` across the source assembly
    // and every referenced assembly. Compilation.GetSymbolsWithName only searches source
    // declarations — missing types defined in NuGet references or project dependencies.
    public static IEnumerable<INamedTypeSymbol> FindByName(Compilation compilation, string name)
    {
        foreach (var type in EnumerateAll(compilation.Assembly.GlobalNamespace))
        {
            if (string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                yield return type;
            }
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            foreach (var type in EnumerateAll(assembly.GlobalNamespace))
            {
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    yield return type;
                }
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> EnumerateAll(INamespaceSymbol ns)
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

    static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
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
