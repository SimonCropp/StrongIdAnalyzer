// Whether `[Id<tag>]` would compile on `site`'s declaration: the generic attribute is only
// emitted from C# 11, and the name has to bind to exactly one non-generic, non-static
// type there (CS0104 / CS0305 / CS0718 otherwise). Shared by SIA009 and the Fix clauses
// of SIA001–SIA003 and SIA006, and mirrored by the fixer's ShouldUseGenericAsync.
static class GenericTag
{
    public static bool Compiles(Compilation compilation, ISymbol? site, string tag)
    {
        if (site is null ||
            site.DeclaringSyntaxReferences.IsEmpty)
        {
            return false;
        }

        var reference = site.DeclaringSyntaxReferences[0];
        var tree = reference.SyntaxTree;
        if (!compilation.ContainsSyntaxTree(tree))
        {
            return false;
        }

        return Compiles(compilation.GetSemanticModel(tree), reference.Span.Start, tag);
    }

    // At a location rather than a declaration: the diagnostic's own position, used when
    // rendering what a side currently carries.
    public static bool Compiles(Compilation compilation, Location location, string tag)
    {
        var tree = location.SourceTree;
        if (tree is null ||
            !compilation.ContainsSyntaxTree(tree))
        {
            return false;
        }

        return Compiles(compilation.GetSemanticModel(tree), location.SourceSpan.Start, tag);
    }

    public static bool Compiles(SemanticModel model, int position, string tag)
    {
        if (model.SyntaxTree.Options is not CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp11 } ||
            !SyntaxFacts.IsValidIdentifier(tag))
        {
            return false;
        }

        INamedTypeSymbol? only = null;
        foreach (var symbol in model.LookupNamespacesAndTypes(position, name: tag))
        {
            if (symbol is not INamedTypeSymbol named)
            {
                continue;
            }

            if (only is not null)
            {
                return false;
            }

            only = named;
        }

        return only is { Arity: 0, IsStatic: false, TypeKind: not TypeKind.Error };
    }
}
