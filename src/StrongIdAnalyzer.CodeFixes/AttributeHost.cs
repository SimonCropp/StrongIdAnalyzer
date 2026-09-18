// Host-level syntax helpers: finding the property/field/parameter that owns an Id-family
// attribute, inspecting existing attributes on it, and producing human-readable labels
// for fix titles. Shared by AddIdCodeFixProvider and IdAttributeFactory.
static class AttributeHost
{
    // IFieldSymbol.DeclaringSyntaxReferences points at the VariableDeclaratorSyntax
    // (e.g. `a` in `public Guid a, b;`). Attribute lists live on the enclosing
    // FieldDeclarationSyntax, which would apply the attribute to *all* declarators.
    public static SyntaxNode? Find(SyntaxNode node)
    {
        // A tuple element declares no attribute host of its own: it lives inside a tuple
        // TYPE, so climbing out of it lands on whatever parameter or property that type
        // annotates. `[Id]` there describes the whole tuple (leaving the diagnostic in
        // place) and a rename rewrites the element while the title says "parameter".
        if (node.FirstAncestorOrSelf<TupleElementSyntax>() is not null)
        {
            return null;
        }

        var declarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (declarator is not null)
        {
            if (declarator.Parent is VariableDeclarationSyntax { Variables.Count: > 1 })
            {
                return null;
            }

            return declarator.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        }

        return node.FirstAncestorOrSelf<SyntaxNode>(ancestor =>
            ancestor is PropertyDeclarationSyntax or ParameterSyntax);
    }

    // Walk up from an attribute to the property/field/parameter it decorates, so fix
    // titles can name the owner. Returns null for attribute targets we don't fix
    // against (e.g. method return attributes) — callers fall back to a generic title.
    public static SyntaxNode? FindOwner(AttributeSyntax attribute) =>
        attribute.Parent?.Parent switch
        {
            PropertyDeclarationSyntax property => property,
            FieldDeclarationSyntax field => field,
            ParameterSyntax parameter => parameter,
            _ => null
        };

    // Human-readable "<kind> '<name>'" label (e.g. "parameter 'bidId'") so fix titles
    // name both the role and the identifier being acted on — screenshots with just
    // "Rename to 'treasuryBidId'" leave users guessing which side of the call is
    // being renamed.
    public static string Describe(SyntaxNode host) =>
        host switch
        {
            PropertyDeclarationSyntax property => $"property '{property.Identifier.Text}'",
            FieldDeclarationSyntax { Declaration.Variables.Count: > 0 } field =>
                $"field '{field.Declaration.Variables[0].Identifier.Text}'",
            ParameterSyntax parameter => $"parameter '{parameter.Identifier.Text}'",
            _ => "declaration"
        };

    public static SyntaxList<AttributeListSyntax> GetAttributeLists(SyntaxNode host) =>
        host switch
        {
            PropertyDeclarationSyntax property => property.AttributeLists,
            FieldDeclarationSyntax field => field.AttributeLists,
            ParameterSyntax parameter => parameter.AttributeLists,
            _ => default
        };

    public static bool HasIdFamilyAttribute(SyntaxNode host) =>
        FindIdFamilyAttribute(host) is not null;

    public static AttributeSyntax? FindIdFamilyAttribute(SyntaxNode host)
    {
        foreach (var list in GetAttributeLists(host))
        {
            foreach (var attribute in list.Attributes)
            {
                var name = GetAttributeName(attribute.Name);
                if (name is "Id" or "IdAttribute" or "UnionId" or "UnionIdAttribute")
                {
                    return attribute;
                }
            }
        }

        return null;
    }

    public static string GetAttributeName(NameSyntax name) =>
        name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => name.ToString()
        };

    // Rename heuristic: produce `<tag>Id`, matching the first-character case of the
    // current identifier (camelCase for parameters like `id`, PascalCase for a property
    // like `Value`). A field's underscore prefix is punctuation, not part of the name —
    // it is carried through untouched and the casing decision is made on what follows it
    // (`_bidId` → `_treasuryBidId`). Skips property/field named exactly "Id" — its
    // convention tag is the containing type's name, so `<tag>Id` would require moving the
    // declaration to a different type; the same holds for its underscore-prefixed field
    // form (`_id`). Parameters named `id` are fine to rename since they have no
    // containing-type convention.
    public static bool TryGetRenameTarget(SyntaxNode host, string tag, out string newName)
    {
        newName = "";
        if (tag.Length == 0 || !SyntaxFacts.IsValidIdentifier(tag))
        {
            return false;
        }

        var currentName = host switch
        {
            PropertyDeclarationSyntax property => property.Identifier.Text,
            FieldDeclarationSyntax { Declaration.Variables.Count: 1 } field =>
                field.Declaration.Variables[0].Identifier.Text,
            ParameterSyntax parameter => parameter.Identifier.Text,
            _ => null
        };

        if (currentName is null)
        {
            return false;
        }

        var prefixLength = 0;
        if (host is FieldDeclarationSyntax)
        {
            while (prefixLength < currentName.Length && currentName[prefixLength] == '_')
            {
                prefixLength++;
            }

            // An all-underscore name (`_`) has no body to case-adjust — leave it whole
            // and let the normal `<tag>Id` rename apply.
            if (prefixLength == currentName.Length)
            {
                prefixLength = 0;
            }
        }

        var prefix = currentName.Substring(0, prefixLength);
        var bareName = currentName.Substring(prefixLength);

        if (host is not ParameterSyntax &&
            string.Equals(bareName, "Id", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstIsLower = char.IsLower(bareName[0]);
        var adjusted = firstIsLower
            ? char.ToLowerInvariant(tag[0]) + tag.Substring(1)
            : char.ToUpperInvariant(tag[0]) + tag.Substring(1);
        var candidate = prefix + adjusted + "Id";
        if (candidate == currentName)
        {
            return false;
        }

        // The rename is only a fix if the naming convention reads the new name back as
        // the same tag. It cannot for a tag that starts lower-case: the convention
        // upper-cases the first character, so `[Id("customer")]` renamed to `customerId`
        // infers "Customer", the tags still differ ordinally, and the user is left with
        // an SIA001 where they had an SIA003.
        if (!ConventionReadsBack(candidate, tag))
        {
            return false;
        }

        newName = candidate;
        return true;
    }

    // Mirror of the analyzer's `<Xxx>Id` rule — the two projects share no code, so the
    // round-trip is checked against a local copy rather than assumed.
    static bool ConventionReadsBack(string name, string tag)
    {
        var bare = name.TrimStart('_');
        if (bare.Length <= 2 ||
            !bare.EndsWith("Id", StringComparison.Ordinal))
        {
            return false;
        }

        var inferred = bare.Substring(0, bare.Length - 2);
        if (char.IsLower(inferred[0]))
        {
            inferred = char.ToUpperInvariant(inferred[0]) + inferred.Substring(1);
        }

        return string.Equals(inferred, tag, StringComparison.Ordinal);
    }
}
