namespace StrongIdAnalyzer;

// Syntax builders for `[Id("...")]` and `[UnionId("...", ...)]`. Kept separate from
// the code-fix orchestration so the provider only deals with when to apply an edit,
// not how to shape the attribute node.
static class IdAttributeFactory
{
    public static SyntaxNode? AddIdAttribute(SyntaxNode host, string value, bool preferGeneric = false) =>
        AppendAttributeList(host, BuildList(BuildIdAttribute(value, preferGeneric)));

    public static SyntaxNode? AddUnionIdAttribute(SyntaxNode host, string[] values, bool preferGeneric = false) =>
        AppendAttributeList(host, BuildList(BuildUnionId(values, preferGeneric)));

    // Replace the first existing Id/UnionId attribute on the host with `[Id(value)]`,
    // preserving its trivia. If no such attribute exists, append a new list instead.
    // When the existing attribute uses the generic form `[Id<X>]`, emit a generic
    // replacement `[Id<value>]` so the codefix preserves the author's chosen style.
    public static SyntaxNode? ReplaceOrAddIdAttribute(SyntaxNode host, string value, bool preferGeneric = false)
    {
        var existing = AttributeHost.FindIdFamilyAttribute(host);
        if (existing is not null)
        {
            var useGeneric = preferGeneric || IsGenericAttribute(existing);
            return host.ReplaceNode(existing, BuildReplacement(value, existing, useGeneric));
        }

        return AppendAttributeList(host, BuildList(BuildIdAttribute(value, preferGeneric)));
    }

    // Build an `[Id(value)]` (or `[Id<value>]`) attribute node that inherits the trivia
    // of an existing attribute — used when replacing in place (e.g. SIA006 UnionId → Id
    // rewrite, or SIA001 mismatch fix).
    public static AttributeSyntax BuildReplacement(string value, AttributeSyntax existing, bool preferGeneric = false) =>
        BuildIdAttribute(value, preferGeneric || IsGenericAttribute(existing))
            .WithTriviaFrom(existing)
            .WithAdditionalAnnotations(Formatter.Annotation);

    public static bool IsGenericAttribute(AttributeSyntax attribute) =>
        attribute.Name switch
        {
            GenericNameSyntax => true,
            QualifiedNameSyntax { Right: GenericNameSyntax } => true,
            _ => false
        };

    static AttributeListSyntax BuildList(AttributeSyntax attribute) =>
        AttributeList(SingletonSeparatedList(attribute))
            .WithAdditionalAnnotations(Formatter.Annotation);

    static AttributeSyntax BuildIdAttribute(string value, bool useGeneric) =>
        useGeneric && IsValidIdentifier(value) ? BuildIdGeneric(value) : BuildId(value);

    static AttributeSyntax BuildId(string value) =>
        Attribute(IdentifierName("Id"))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(StringArg(value))));

    static AttributeSyntax BuildIdGeneric(string value) =>
        Attribute(
            GenericName(Identifier("Id"))
                .WithTypeArgumentList(
                    TypeArgumentList(SingletonSeparatedList<TypeSyntax>(IdentifierName(value)))));

    static AttributeSyntax BuildUnionId(string[] values, bool useGeneric) =>
        useGeneric && values.Length >= 2 && values.All(IsValidIdentifier)
            ? BuildUnionIdGeneric(values)
            : BuildUnionIdString(values);

    static AttributeSyntax BuildUnionIdString(string[] values) =>
        Attribute(IdentifierName("UnionId"))
            .WithArgumentList(AttributeArgumentList(SeparatedList(values.Select(StringArg))));

    static AttributeSyntax BuildUnionIdGeneric(string[] values) =>
        Attribute(
            GenericName(Identifier("UnionId"))
                .WithTypeArgumentList(
                    TypeArgumentList(SeparatedList<TypeSyntax>(values.Select(_ => IdentifierName(_))))));

    static bool IsValidIdentifier(string value) =>
        SyntaxFacts.IsValidIdentifier(value);

    static AttributeArgumentSyntax StringArg(string value) =>
        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value)));

    static SyntaxNode? AppendAttributeList(SyntaxNode host, AttributeListSyntax list) =>
        host switch
        {
            PropertyDeclarationSyntax property => property.AddAttributeLists(list),
            FieldDeclarationSyntax field => field.AddAttributeLists(list),
            ParameterSyntax parameter => parameter.AddAttributeLists(list),
            _ => null
        };
}
