namespace StrongIdAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddIdCodeFixProvider))]
[Shared]
public class AddIdCodeFixProvider : CodeFixProvider
{
    // Kept in sync with IdMismatchAnalyzer. Duplicated instead of shared to keep
    // the analyzer project free of a back-reference from the codefix project.
    const string valueKey = "IdValue";
    const string targetValueKey = "IdValueTarget";
    const string sourceValueKey = "IdValueSource";
    const string idMismatchId = "SIA001";
    const string missingSourceIdId = "SIA002";
    const string droppedIdId = "SIA003";
    const string redundantIdId = "SIA005";
    const string singletonUnionId = "SIA006";

    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        idMismatchId,
        missingSourceIdId,
        droppedIdId,
        redundantIdId,
        singletonUnionId
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

            if (diagnostic.Id == singletonUnionId)
            {
                await RegisterReplaceUnionWithIdFix(context, diagnostic)
                    .ConfigureAwait(false);
                continue;
            }

            if (diagnostic.Id == idMismatchId)
            {
                await RegisterMismatchFixes(context, diagnostic)
                    .ConfigureAwait(false);
                continue;
            }

            await RegisterAddFix(context, diagnostic)
                .ConfigureAwait(false);
        }
    }

    static async Task RegisterMismatchFixes(CodeFixContext context, Diagnostic diagnostic)
    {
        if (diagnostic.AdditionalLocations.Count == 0)
        {
            return;
        }

        // Slot 0 is the target declaration, slot 1 (if present) is the source.
        // Each side gets offered the OTHER side's tag as the replacement value.
        // Older analyzer versions only populate slot 0 and IdValue; the typed keys
        // fall back to IdValue (target) or skip (source) in that case.
        var targetValue = ReadProperty(diagnostic, targetValueKey) ?? ReadProperty(diagnostic, valueKey);
        var sourceValue = ReadProperty(diagnostic, sourceValueKey);

        await TryRegisterSideFix(
                context,
                diagnostic,
                slot: 0,
                value: targetValue)
            .ConfigureAwait(false);

        if (diagnostic.AdditionalLocations.Count > 1)
        {
            await TryRegisterSideFix(
                    context,
                    diagnostic,
                    slot: 1,
                    value: sourceValue)
                .ConfigureAwait(false);
        }
    }

    static string? ReadProperty(Diagnostic diagnostic, string key) =>
        diagnostic.Properties.TryGetValue(key, out var value) ? value : null;

    static async Task TryRegisterSideFix(
        CodeFixContext context,
        Diagnostic diagnostic,
        int slot,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        var declarationLocation = diagnostic.AdditionalLocations[slot];
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
        var host = FindAttributeHost(declarationNode);
        if (host is null)
        {
            return;
        }

        var hasExplicit = HasIdFamilyAttribute(host);
        var hostDescription = DescribeHost(host);
        var actionTitle = hasExplicit
            ? $"Change attribute on {hostDescription} to [Id(\"{value}\")]"
            : $"Add [Id(\"{value}\")] to {hostDescription}";

        // Equivalence keys include the slot so target-side and source-side fixes with
        // the same literal tag don't collide in Fix All / multi-diagnostic scenarios.
        context.RegisterCodeFix(
            CodeAction.Create(
                actionTitle,
                cancel => ChangeOrAddAttributeAsync(
                    context.Document.Project.Solution,
                    declarationLocation,
                    value,
                    cancel),
                equivalenceKey: $"ChangeId:{slot}:{value}"),
            diagnostic);

        if (!hasExplicit && TryGetRenameTarget(host, value, out var newName))
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Rename {hostDescription} to '{newName}'",
                    cancel => RenameAsync(
                        context.Document,
                        declarationLocation,
                        newName,
                        cancel),
                    equivalenceKey: $"RenameId:{slot}:{newName}"),
                diagnostic);
        }
    }

    // Human-readable "<kind> '<name>'" label (e.g. "parameter 'bidId'") so fix titles
    // name both the role and the identifier being acted on — screenshots with just
    // "Rename to 'treasuryBidId'" leave users guessing which side of the call is
    // being renamed.
    static string DescribeHost(SyntaxNode host)
    {
        switch (host)
        {
            case PropertyDeclarationSyntax property:
                return $"property '{property.Identifier.Text}'";
            case FieldDeclarationSyntax { Declaration.Variables.Count: > 0 } field:
                return $"field '{field.Declaration.Variables[0].Identifier.Text}'";
            case ParameterSyntax parameter:
                return $"parameter '{parameter.Identifier.Text}'";
            default:
                return "declaration";
        }
    }

    static bool HasIdFamilyAttribute(SyntaxNode host)
    {
        var lists = host switch
        {
            PropertyDeclarationSyntax property => property.AttributeLists,
            FieldDeclarationSyntax field => field.AttributeLists,
            ParameterSyntax parameter => parameter.AttributeLists,
            _ => default
        };

        foreach (var list in lists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = GetAttributeName(attribute.Name);
                if (name is "Id" or "IdAttribute" or "UnionId" or "UnionIdAttribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    static string GetAttributeName(NameSyntax name) =>
        name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => name.ToString()
        };

    // Rename heuristic: if the host name ends with "Id" (e.g. `bidId`, `BidId`), produce
    // `<tag>Id` with the first character matching the original's case. Skips when the
    // host is named just "Id" — fixing that would require renaming the containing type.
    static bool TryGetRenameTarget(SyntaxNode host, string tag, out string newName)
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

    static async Task<Solution> ChangeOrAddAttributeAsync(
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
        var host = FindAttributeHost(declarationNode);
        if (host is null)
        {
            return solution;
        }

        var newHost = ReplaceOrAddIdAttribute(host, value);
        if (newHost is null)
        {
            return solution;
        }

        var newRoot = root.ReplaceNode(host, newHost);
        var newDocument = document.WithSyntaxRoot(newRoot);
        newDocument = await Formatter
            .FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancel)
            .ConfigureAwait(false);
        return newDocument.Project.Solution;
    }

    static async Task<Solution> RenameAsync(
        Document document,
        Location declarationLocation,
        string newName,
        Cancel cancel)
    {
        var semanticModel = await document
            .GetSemanticModelAsync(cancel)
            .ConfigureAwait(false);
        if (semanticModel is null)
        {
            return document.Project.Solution;
        }

        var root = await document
            .GetSyntaxRootAsync(cancel)
            .ConfigureAwait(false);
        if (root is null)
        {
            return document.Project.Solution;
        }

        var declarationNode = root.FindNode(declarationLocation.SourceSpan);
        var symbol = semanticModel.GetDeclaredSymbol(declarationNode, cancel);
        if (symbol is null)
        {
            return document.Project.Solution;
        }

        return await Renamer
            .RenameSymbolAsync(
                document.Project.Solution,
                symbol,
                new(),
                newName,
                cancel)
            .ConfigureAwait(false);
    }

    static SyntaxNode? ReplaceOrAddIdAttribute(SyntaxNode host, string value)
    {
        var lists = host switch
        {
            PropertyDeclarationSyntax property => property.AttributeLists,
            FieldDeclarationSyntax field => field.AttributeLists,
            ParameterSyntax parameter => parameter.AttributeLists,
            _ => default
        };

        AttributeSyntax? existing = null;
        foreach (var list in lists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = GetAttributeName(attribute.Name);
                if (name is "Id" or "IdAttribute" or "UnionId" or "UnionIdAttribute")
                {
                    existing = attribute;
                    break;
                }
            }

            if (existing is not null)
            {
                break;
            }
        }

        var argument = AttributeArgument(
            LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value)));
        var replacement = Attribute(IdentifierName("Id"))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(argument)));

        if (existing is not null)
        {
            return host.ReplaceNode(
                existing,
                replacement.WithTriviaFrom(existing).WithAdditionalAnnotations(Formatter.Annotation));
        }

        var attributeList = AttributeList(SingletonSeparatedList(replacement))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return host switch
        {
            PropertyDeclarationSyntax property => property.AddAttributeLists(attributeList),
            FieldDeclarationSyntax field => field.AddAttributeLists(attributeList),
            ParameterSyntax parameter => parameter.AddAttributeLists(attributeList),
            _ => null
        };
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
        var host = FindAttributeHost(declarationNode);
        if (host is null)
        {
            return;
        }

        var values = value.Split('|');

        // When the source has [UnionId(a, b, ...)], offer a matching [UnionId] fix first
        // plus one [Id(x)] fix per tag — picking which option to accept is a human
        // judgement call (same reasoning as the StringSyntax pipe split).
        if (values.Length > 1)
        {
            var unionArgs = string.Join(", ", values.Select(_ => $"\"{_}\""));
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [UnionId({unionArgs})] to {DescribeHost(host)}",
                    cancel => AddUnionAttributeAsync(
                        context.Document.Project.Solution,
                        declarationLocation,
                        values,
                        cancel),
                    equivalenceKey: $"AddUnionId:{value}"),
                diagnostic);
        }

        foreach (var singleValue in values)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [Id(\"{singleValue}\")] to {DescribeHost(host)}",
                    cancel => AddAttributeAsync(
                        context.Document.Project.Solution,
                        declarationLocation,
                        singleValue,
                        cancel),
                    equivalenceKey: $"AddId:{singleValue}"),
                diagnostic);
        }
    }

    static async Task RegisterReplaceUnionWithIdFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(valueKey, out var value) || value is null)
        {
            return;
        }

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

        var title = FindAttributeOwner(attribute) is { } owner
            ? $"Replace [UnionId] on {DescribeHost(owner)} with [Id(\"{value}\")]"
            : $"Replace [UnionId] with [Id(\"{value}\")]";

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancel => ReplaceUnionWithIdAsync(context.Document, location, value, cancel),
                equivalenceKey: $"ReplaceUnionWithId:{value}"),
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

        var title = FindAttributeOwner(attribute) is { } owner
            ? $"Remove redundant [Id] from {DescribeHost(owner)}"
            : "Remove redundant [Id] attribute";

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancel => RemoveAttributeAsync(context.Document, location, cancel),
                equivalenceKey: "RemoveRedundantId"),
            diagnostic);
    }

    // Walk up from an attribute to the property/field/parameter it decorates, so fix
    // titles can name the owner. Returns null for attribute targets we don't fix
    // against (e.g. method return attributes) — callers fall back to a generic title.
    static SyntaxNode? FindAttributeOwner(AttributeSyntax attribute) =>
        attribute.Parent?.Parent switch
        {
            PropertyDeclarationSyntax property => property,
            FieldDeclarationSyntax field => field,
            ParameterSyntax parameter => parameter,
            _ => null
        };

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

    static async Task<Document> ReplaceUnionWithIdAsync(
        Document document,
        Location location,
        string value,
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
        var oldAttribute = node.FirstAncestorOrSelf<AttributeSyntax>();
        if (oldAttribute is null)
        {
            return document;
        }

        var argument = AttributeArgument(
            LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value)));
        var newAttribute = Attribute(IdentifierName("Id"))
            .WithArgumentList(AttributeArgumentList(SingletonSeparatedList(argument)))
            .WithTriviaFrom(oldAttribute)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(oldAttribute, newAttribute);
        return document.WithSyntaxRoot(newRoot);
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

    static async Task<Solution> AddUnionAttributeAsync(
        Solution solution,
        Location declarationLocation,
        string[] values,
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

        var newTargetNode = AddUnionIdAttribute(targetNode, values);
        if (newTargetNode is null)
        {
            return solution;
        }

        var newRoot = root.ReplaceNode(targetNode, newTargetNode);
        var newDocument = document.WithSyntaxRoot(newRoot);

        newDocument = await Formatter
            .FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancel)
            .ConfigureAwait(false);

        return newDocument.Project.Solution;
    }

    static SyntaxNode? AddUnionIdAttribute(SyntaxNode host, string[] values)
    {
        var arguments = values.Select(value =>
            AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(value))));

        var attribute = Attribute(IdentifierName("UnionId"))
            .WithArgumentList(AttributeArgumentList(SeparatedList(arguments)));

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
