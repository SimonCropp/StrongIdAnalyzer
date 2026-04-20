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
        var host = AttributeHost.Find(declarationNode);
        if (host is null)
        {
            return;
        }

        var hasExplicit = AttributeHost.HasIdFamilyAttribute(host);
        var hostDescription = AttributeHost.Describe(host);
        var preferGeneric = await PreferGenericAsync(context, diagnostic, host).ConfigureAwait(false);
        var rendered = preferGeneric && SyntaxFacts.IsValidIdentifier(value)
            ? $"[Id<{value}>]"
            : $"[Id(\"{value}\")]";
        var actionTitle = hasExplicit
            ? $"Change attribute on {hostDescription} to {rendered}"
            : $"Add {rendered} to {hostDescription}";

        // Equivalence keys include the slot so target-side and source-side fixes with
        // the same literal tag don't collide in Fix All / multi-diagnostic scenarios.
        context.RegisterCodeFix(
            CodeAction.Create(
                actionTitle,
                cancel => ChangeOrAddAttributeAsync(
                    context.Document.Project.Solution,
                    declarationLocation,
                    value,
                    preferGeneric,
                    cancel),
                equivalenceKey: $"ChangeId:{slot}:{value}"),
            diagnostic);

        if (!hasExplicit && AttributeHost.TryGetRenameTarget(host, value, out var newName))
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

    static async Task<Solution> ChangeOrAddAttributeAsync(
        Solution solution,
        Location declarationLocation,
        string value,
        bool preferGeneric,
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
        var host = AttributeHost.Find(declarationNode);
        if (host is null)
        {
            return solution;
        }

        var newHost = IdAttributeFactory.ReplaceOrAddIdAttribute(host, value, preferGeneric);
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

    // Decide whether the fix output should use the generic attribute form `[Id<X>]`.
    // Triggered when *any* Id-family attribute on either the host being fixed or the
    // other side of the mismatch already uses generic form — preserves the author's
    // chosen style instead of silently converting `[Id<Department>]` into `[Id("Department")]`.
    static async Task<bool> PreferGenericAsync(
        CodeFixContext context,
        Diagnostic diagnostic,
        SyntaxNode host)
    {
        if (HasGenericIdAttribute(host))
        {
            return true;
        }

        foreach (var location in diagnostic.AdditionalLocations)
        {
            if (!location.IsInSource || location.SourceTree is null)
            {
                continue;
            }

            var tree = location.SourceTree;
            var root = await tree
                .GetRootAsync(context.CancellationToken)
                .ConfigureAwait(false);
            var node = root.FindNode(location.SourceSpan);
            var otherHost = AttributeHost.Find(node);
            if (otherHost is not null && HasGenericIdAttribute(otherHost))
            {
                return true;
            }
        }

        return false;
    }

    static bool HasGenericIdAttribute(SyntaxNode host)
    {
        var attribute = AttributeHost.FindIdFamilyAttribute(host);
        return attribute is not null && IdAttributeFactory.IsGenericAttribute(attribute);
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
        var host = AttributeHost.Find(declarationNode);
        if (host is null)
        {
            return;
        }

        var values = value.Split('|');
        var hostDescription = AttributeHost.Describe(host);
        var preferGeneric = await PreferGenericAsync(context, diagnostic, host).ConfigureAwait(false);

        // When the source has [UnionId(a, b, ...)], offer a matching [UnionId] fix first
        // plus one [Id(x)] fix per tag — picking which option to accept is a human
        // judgement call (same reasoning as the StringSyntax pipe split).
        if (values.Length > 1)
        {
            var useGenericUnion = preferGeneric && values.All(SyntaxFacts.IsValidIdentifier);
            var unionArgs = useGenericUnion
                ? string.Join(", ", values)
                : string.Join(", ", values.Select(_ => $"\"{_}\""));
            var unionRendered = useGenericUnion ? $"[UnionId<{unionArgs}>]" : $"[UnionId({unionArgs})]";
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add {unionRendered} to {hostDescription}",
                    cancel => AddUnionAttributeAsync(
                        context.Document.Project.Solution,
                        declarationLocation,
                        values,
                        preferGeneric,
                        cancel),
                    equivalenceKey: $"AddUnionId:{value}"),
                diagnostic);
        }

        foreach (var singleValue in values)
        {
            var rendered = preferGeneric && SyntaxFacts.IsValidIdentifier(singleValue)
                ? $"[Id<{singleValue}>]"
                : $"[Id(\"{singleValue}\")]";
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add {rendered} to {hostDescription}",
                    cancel => AddAttributeAsync(
                        context.Document.Project.Solution,
                        declarationLocation,
                        singleValue,
                        preferGeneric,
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

        var preferGeneric = IdAttributeFactory.IsGenericAttribute(attribute) && SyntaxFacts.IsValidIdentifier(value);
        var rendered = preferGeneric ? $"[Id<{value}>]" : $"[Id(\"{value}\")]";
        var title = AttributeHost.FindOwner(attribute) is { } owner
            ? $"Replace [UnionId] on {AttributeHost.Describe(owner)} with {rendered}"
            : $"Replace [UnionId] with {rendered}";

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancel => ReplaceUnionWithIdAsync(context.Document, location, value, preferGeneric, cancel),
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

        var title = AttributeHost.FindOwner(attribute) is { } owner
            ? $"Remove redundant [Id] from {AttributeHost.Describe(owner)}"
            : "Remove redundant [Id] attribute";

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancel => RemoveAttributeAsync(context.Document, location, cancel),
                equivalenceKey: "RemoveRedundantId"),
            diagnostic);
    }

    static async Task<Solution> AddAttributeAsync(
        Solution solution,
        Location declarationLocation,
        string value,
        bool preferGeneric,
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
        var targetNode = AttributeHost.Find(declarationNode);
        if (targetNode is null)
        {
            return solution;
        }

        var newTargetNode = IdAttributeFactory.AddIdAttribute(targetNode, value, preferGeneric);
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

    static async Task<Solution> AddUnionAttributeAsync(
        Solution solution,
        Location declarationLocation,
        string[] values,
        bool preferGeneric,
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
        var targetNode = AttributeHost.Find(declarationNode);
        if (targetNode is null)
        {
            return solution;
        }

        var newTargetNode = IdAttributeFactory.AddUnionIdAttribute(targetNode, values, preferGeneric);
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

    static async Task<Document> ReplaceUnionWithIdAsync(
        Document document,
        Location location,
        string value,
        bool preferGeneric,
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

        // Replace the exact attribute at the diagnostic location rather than "first
        // Id-family attribute on the owner" — a host can legitimately carry multiple
        // attributes and only the one being diagnosed should be rewritten.
        var newAttribute = IdAttributeFactory.BuildReplacement(value, oldAttribute, preferGeneric);
        return document.WithSyntaxRoot(root.ReplaceNode(oldAttribute, newAttribute));
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
}
