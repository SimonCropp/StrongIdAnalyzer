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

    // Rename heuristic: if the host name ends with "Id" (e.g. `bidId`, `BidId`), produce
    // `<tag>Id` with the first character matching the original's case. Skips when the
    // host is named just "Id" — fixing that would require renaming the containing type.
    public static bool TryGetRenameTarget(SyntaxNode host, string tag, out string newName)
    {
        newName = "";
        if (tag.Length == 0)
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

        if (currentName.Length <= 2 ||
            !currentName.EndsWith("Id", StringComparison.Ordinal))
        {
            return false;
        }

        var firstIsLower = char.IsLower(currentName[0]);
        var adjusted = firstIsLower
            ? char.ToLowerInvariant(tag[0]) + tag.Substring(1)
            : char.ToUpperInvariant(tag[0]) + tag.Substring(1);
        newName = adjusted + "Id";
        return newName != currentName;
    }
}
