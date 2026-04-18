namespace StrongIdAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddIdCodeFixProvider))]
[Shared]
public class AddIdCodeFixProvider : CodeFixProvider
{
    // Kept in sync with IdMismatchAnalyzer. Duplicated instead of shared to keep
    // the analyzer project free of a back-reference from the codefix project.
    const string valueKey = "IdValue";
    const string missingSourceIdId = "SIA002";
    const string droppedIdId = "SIA003";
    const string redundantIdId = "SIA005";

    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        missingSourceIdId,
        droppedIdId,
        redundantIdId
    ];

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id == redundantIdId)
            {
                await RegisterRemoveFix(context, diagnostic)
                    .ConfigureAwait(false);
                continue;
            }

            await RegisterAddFix(context, diagnostic)
                .ConfigureAwait(false);
        }
    }

    static async Task RegisterAddFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (diagnostic.AdditionalLocations.Count == 0)
        {
            return;
        }

        if (!diagnostic.Properties.TryGetValue(valueKey, out var value) ||
            value is null)
        {
            return;
        }

        var declarationLocation = diagnostic.AdditionalLocations[0];
        if (!declarationLocation.IsInSource)
        {
            return;
        }

        var declarationTree = declarationLocation.SourceTree;
        if (declarationTree is null)
        {
            return;
        }

        var declarationRoot = await declarationTree
            .GetRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var declarationNode = declarationRoot.FindNode(declarationLocation.SourceSpan);
        if (FindAttributeHost(declarationNode) is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add [Id(\"{value}\")]",
                cancel => AddAttributeAsync(
                    context.Document.Project.Solution,
                    declarationLocation,
                    value,
                    cancel),
                equivalenceKey: $"AddId:{value}"),
            diagnostic);
    }

    static async Task RegisterRemoveFix(CodeFixContext context, Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        var tree = location.SourceTree;
        if (tree is null)
        {
            return;
        }

        var root = await tree
            .GetRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var node = root.FindNode(location.SourceSpan);
        var attribute = node.FirstAncestorOrSelf<AttributeSyntax>();
        if (attribute is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove redundant [Id] attribute",
                cancel => RemoveAttributeAsync(context.Document, location, cancel),
                equivalenceKey: "RemoveRedundantId"),
            diagnostic);
    }

    static async Task<Solution> AddAttributeAsync(
        Solution solution,
        Location declarationLocation,
        string value,
        Cancel cancel)
    {
        var document = solution.GetDocument(declarationLocation.SourceTree);
        if (document is null)
        {
            return solution;
        }

        var root = await document
            .GetSyntaxRootAsync(cancel)
            .ConfigureAwait(false);
        if (root is null)
        {
            return solution;
        }

        var declarationNode = root.FindNode(declarationLocation.SourceSpan);
        var targetNode = FindAttributeHost(declarationNode);
        if (targetNode is null)
        {
            return solution;
        }

        var newTargetNode = AddIdAttribute(targetNode, value);
        if (newTargetNode is null)
        {
            return solution;
        }

        var newRoot = root.ReplaceNode(targetNode, newTargetNode);
        var newDocument = document.WithSyntaxRoot(newRoot);

        // Deliberately no ImportAdder / Simplifier pass. The attribute is inserted as the
        // short name `Id` and resolves via whatever using (file-local or `global using`)
        // the consumer already has. Adding an explicit using here was fighting with
        // Rider/VS "remove unnecessary usings" cleanup when a global using was in scope —
        // each successive fix left behind a blank line of trivia where the redundant
        // local using had been.
        newDocument = await Formatter
            .FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancel)
            .ConfigureAwait(false);

        return newDocument.Project.Solution;
    }

    static async Task<Document> RemoveAttributeAsync(
        Document document,
        Location location,
        Cancel cancel)
    {
        var root = await document
            .GetSyntaxRootAsync(cancel)
            .ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var node = root.FindNode(location.SourceSpan);
        var attribute = node.FirstAncestorOrSelf<AttributeSyntax>();
        if (attribute is null)
        {
            return document;
        }

        SyntaxNode newRoot;
        if (attribute.Parent is AttributeListSyntax { Attributes.Count: 1 } list)
        {
            // Whole list (e.g. `[Id("Order")]`) is just this attribute — drop the list so we
            // don't leave behind empty brackets on the declaration.
            newRoot = root.RemoveNode(list, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            newRoot = root.RemoveNode(attribute, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        return document.WithSyntaxRoot(newRoot);
    }

    static SyntaxNode? FindAttributeHost(SyntaxNode node)
    {
        // IFieldSymbol.DeclaringSyntaxReferences points at the VariableDeclaratorSyntax
        // (e.g. `a` in `public Guid a, b;`). Attribute lists live on the enclosing
        // FieldDeclarationSyntax, which would apply the attribute to *all* declarators.
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

    static SyntaxNode? AddIdAttribute(SyntaxNode host, string value)
    {
        var attributeName = IdentifierName("Id");

        var argument = AttributeArgument(
            LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                Literal(value)));

        var attribute = Attribute(attributeName)
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(argument)));

        var attributeList = AttributeList(SingletonSeparatedList(attribute))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return host switch
        {
            PropertyDeclarationSyntax property => property.AddAttributeLists(attributeList),
            FieldDeclarationSyntax field => field.AddAttributeLists(attributeList),
            ParameterSyntax parameter => parameter.AddAttributeLists(attributeList),
            _ => null
        };
    }
}
