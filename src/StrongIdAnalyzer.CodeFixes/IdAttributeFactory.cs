namespace StrongIdAnalyzer;

// Syntax builders for `[Id("...")]` and `[UnionId("...", ...)]`. Kept separate from
// the code-fix orchestration so the provider only deals with when to apply an edit,
// not how to shape the attribute node.
static class IdAttributeFactory
{
    public static SyntaxNode? AddIdAttribute(SyntaxNode host, string value) =>
        AppendAttributeList(host, BuildList(BuildId(value)));

    public static SyntaxNode? AddUnionIdAttribute(SyntaxNode host, string[] values) =>
        AppendAttributeList(host, BuildList(BuildUnionId(values)));

    // Replace the first existing Id/UnionId attribute on the host with `[Id(value)]`,
    // preserving its trivia. If no such attribute exists, append a new list instead.
    public static SyntaxNode? ReplaceOrAddIdAttribute(SyntaxNode host, string value)
    {
        var existing = AttributeHost.FindIdFamilyAttribute(host);
        if (existing is not null)
        {
            return host.ReplaceNode(existing, BuildReplacement(value, existing));
        }

        return AppendAttributeList(host, BuildList(BuildId(value)));
    }

    // Build an `[Id(value)]` attribute node that inherits the trivia of an existing
    // attribute — used when replacing in place (e.g. SIA006 UnionId → Id rewrite).
    public static AttributeSyntax BuildReplacement(string value, AttributeSyntax existing) =>
        BuildId(value)
            .WithTriviaFrom(existing)
            .WithAdditionalAnnotations(Formatter.Annotation);

    static AttributeListSyntax BuildList(AttributeSyntax attribute) =>
        AttributeList(SingletonSeparatedList(attribute))
            .WithAdditionalAnnotations(Formatter.Annotation);

    static AttributeSyntax BuildId(string value) =>
        Attribute(IdentifierName("Id"))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(StringArg(value))));

    static AttributeSyntax BuildUnionId(string[] values) =>
        Attribute(IdentifierName("UnionId"))
            .WithArgumentList(AttributeArgumentList(SeparatedList(values.Select(StringArg))));

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
