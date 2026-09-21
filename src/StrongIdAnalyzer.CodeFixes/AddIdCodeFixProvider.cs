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
        diagnostic.Properties.GetValueOrDefault(key);

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
        var preferGeneric = await ShouldUseGenericAsync(
            context.Document.Project,
            host,
            value,
            context.CancellationToken).ConfigureAwait(false);
        var rendered = preferGeneric
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
                    context.Document.Project.Id,
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
                        context.Document.Project.Solution,
                        context.Document.Project.Id,
                        declarationLocation,
                        newName,
                        cancel),
                    equivalenceKey: $"RenameId:{slot}:{newName}"),
                diagnostic);
        }
    }

    // The document a declaration lives in. Solution.GetDocument(tree) matches by tree
    // identity, which fails in an out-of-process analyzer host (Rider): the diagnostic
    // carries a tree that was re-parsed on the fix side, so the lookup returned null and
    // every attribute fix quietly returned the solution unchanged — the action appeared
    // in the lightbulb and did nothing. Fall back to the file path, preferring the
    // project the fix was invoked from, the same recovery ShouldUseGenericAsync makes.
    static Document? FindDocument(Solution solution, ProjectId projectId, SyntaxTree? tree)
    {
        if (tree is null)
        {
            return null;
        }

        if (solution.GetDocument(tree) is { } byIdentity)
        {
            return byIdentity;
        }

        var path = tree.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (solution.GetProject(projectId) is { } project &&
            FindByPath(project, path) is { } inProject)
        {
            return inProject;
        }

        foreach (var other in solution.Projects)
        {
            if (FindByPath(other, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    static Document? FindByPath(Project project, string path)
    {
        foreach (var document in project.Documents)
        {
            if (string.Equals(document.FilePath, path, StringComparison.Ordinal))
            {
                return document;
            }
        }

        return null;
    }

    // Root of the declaring document, plus the node the declaration location points at.
    // Returns null when the location cannot be mapped — a span from a document that is
    // no longer in the solution must never be run against a different file's tree.
    static async Task<(Document Document, SyntaxNode Root, SyntaxNode Node)?> FindDeclarationAsync(
        Solution solution,
        ProjectId projectId,
        Location declarationLocation,
        Cancel cancel)
    {
        var document = FindDocument(solution, projectId, declarationLocation.SourceTree);
        if (document is null)
        {
            return null;
        }

        var root = await document
            .GetSyntaxRootAsync(cancel)
            .ConfigureAwait(false);
        if (root is null ||
            !root.FullSpan.Contains(declarationLocation.SourceSpan))
        {
            return null;
        }

        return (document, root, root.FindNode(declarationLocation.SourceSpan));
    }

    static async Task<Solution> ChangeOrAddAttributeAsync(
        Solution solution,
        ProjectId projectId,
        Location declarationLocation,
        string value,
        bool preferGeneric,
        Cancel cancel)
    {
        if (await FindDeclarationAsync(solution, projectId, declarationLocation, cancel).ConfigureAwait(false)
            is not var (document, root, declarationNode))
        {
            return solution;
        }

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

    // Renames through the DECLARING document. Running the declaration's span against the
    // document the diagnostic was raised in renamed whichever unrelated member happened
    // to occupy those offsets in that file — and threw when the span ran past its end.
    static async Task<Solution> RenameAsync(
        Solution solution,
        ProjectId projectId,
        Location declarationLocation,
        string newName,
        Cancel cancel)
    {
        if (await FindDeclarationAsync(solution, projectId, declarationLocation, cancel).ConfigureAwait(false)
            is not var (document, _, declarationNode))
        {
            return solution;
        }

        var semanticModel = await document
            .GetSemanticModelAsync(cancel)
            .ConfigureAwait(false);
        if (semanticModel is null)
        {
            return solution;
        }

        var symbol = semanticModel.GetDeclaredSymbol(declarationNode, cancel);
        if (symbol is null)
        {
            return solution;
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

    // Decide whether the fix output should use the generic attribute form `[Id<X>]`
    // for a specific tag value. Generic form is chosen when the value is a valid
    // C# identifier and exactly one non-generic, non-static type with that name is
    // visible at the host's position.
    //
    // All three qualifiers matter, because `[Id<X>]` has to compile: a generic type
    // needs its type arguments (`[Id<Ledger>]` for `Ledger<TKey>` is CS0305, and a
    // generic entity base makes that shape common), two candidates are CS0104, and a
    // static class cannot be a type argument at all (CS0718). The string form always
    // compiles, so anything short of certain falls back to it.
    //
    // In out-of-process analyzer hosts (Rider) the syntax tree carried by the
    // diagnostic location is re-parsed on the fix side and does not match the
    // live project tree by identity — Solution.GetDocument(tree) returns null,
    // so we previously fell back to string form for every tag. The fix maps the
    // host's span onto the live tree at the same file path before looking up.
    static async Task<bool> ShouldUseGenericAsync(
        Project project,
        SyntaxNode host,
        string value,
        Cancel cancel)
    {
        if (!SyntaxFacts.IsValidIdentifier(value))
        {
            return false;
        }

        var compilation = await project
            .GetCompilationAsync(cancel)
            .ConfigureAwait(false);
        if (compilation is null)
        {
            return false;
        }

        var tree = host.SyntaxTree;

        if (!compilation.SyntaxTrees.Contains(tree))
        {
            var filePath = tree.FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }

            tree = compilation.SyntaxTrees
                .FirstOrDefault(_ => string.Equals(_.FilePath, filePath, StringComparison.Ordinal));
            if (tree is null)
            {
                return false;
            }
        }

        var model = compilation.GetSemanticModel(tree);
        var symbols = model.LookupNamespacesAndTypes(host.SpanStart, name: value);
        INamedTypeSymbol? only = null;
        foreach (var symbol in symbols)
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
        var perValueGeneric = new bool[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            perValueGeneric[i] = await ShouldUseGenericAsync(
                context.Document.Project,
                host,
                values[i],
                context.CancellationToken).ConfigureAwait(false);
        }

        // When the source has [UnionId(a, b, ...)], offer a matching [UnionId] fix first
        // plus one [Id(x)] fix per tag — picking which option to accept is a human
        // judgement call (same reasoning as the StringSyntax pipe split).
        if (values.Length > 1)
        {
            var useGenericUnion = perValueGeneric.All(_ => _);
            var unionArgs = useGenericUnion
                ? string.Join(", ", values)
                : string.Join(", ", values.Select(_ => $"\"{_}\""));
            var unionRendered = useGenericUnion ? $"[UnionId<{unionArgs}>]" : $"[UnionId({unionArgs})]";
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add {unionRendered} to {hostDescription}",
                    cancel => AddUnionAttributeAsync(
                        context.Document.Project.Solution,
                        context.Document.Project.Id,
                        declarationLocation,
                        values,
                        useGenericUnion,
                        cancel),
                    equivalenceKey: $"AddUnionId:{value}"),
                diagnostic);
        }

        for (var i = 0; i < values.Length; i++)
        {
            var singleValue = values[i];
            var useGeneric = perValueGeneric[i];
            var rendered = useGeneric
                ? $"[Id<{singleValue}>]"
                : $"[Id(\"{singleValue}\")]";
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add {rendered} to {hostDescription}",
                    cancel => AddAttributeAsync(
                        context.Document.Project.Solution,
                        context.Document.Project.Id,
                        declarationLocation,
                        singleValue,
                        useGeneric,
                        cancel),
                    equivalenceKey: $"AddId:{singleValue}"),
                diagnostic);
        }

        // Renaming the host to `<Tag>Id` satisfies the naming convention without
        // introducing an attribute. For multi-tag sources (explicit [UnionId] or
        // convention inheritance up a type chain), any single tag satisfies the
        // other side via set containment, so offer one rename per tag.
        var seenRenames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var singleValue in values)
        {
            if (!AttributeHost.TryGetRenameTarget(host, singleValue, out var newName))
            {
                continue;
            }

            if (!seenRenames.Add(newName))
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Rename {hostDescription} to '{newName}'",
                    cancel => RenameAsync(
                        context.Document.Project.Solution,
                        context.Document.Project.Id,
                        declarationLocation,
                        newName,
                        cancel),
                    equivalenceKey: $"RenameId:{newName}"),
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

        var preferGeneric = await ShouldUseGenericAsync(
            context.Document.Project,
            host: attribute,
            value: value,
            cancel: context.CancellationToken).ConfigureAwait(false);
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
        ProjectId projectId,
        Location declarationLocation,
        string value,
        bool preferGeneric,
        Cancel cancel)
    {
        if (await FindDeclarationAsync(solution, projectId, declarationLocation, cancel).ConfigureAwait(false)
            is not var (document, root, declarationNode))
        {
            return solution;
        }

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
        ProjectId projectId,
        Location declarationLocation,
        string[] values,
        bool preferGeneric,
        Cancel cancel)
    {
        if (await FindDeclarationAsync(solution, projectId, declarationLocation, cancel).ConfigureAwait(false)
            is not var (document, root, declarationNode))
        {
            return solution;
        }

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
        if (attribute.Parent
                is AttributeListSyntax
                {
                    Attributes.Count: 1,
                    Parent: { } owner
                } list &&
            owner.GetFirstToken() != list.OpenBracketToken)
        {
            newRoot = RemoveLaterList(root, list);
        }
        else if (attribute.Parent
                 is AttributeListSyntax
                 {
                     Attributes.Count: 1,
                     Parent: { } firstOwner
                 } firstList)
        {
            // Whole list (e.g. `[Id("Order")]`) is just this attribute — drop the list so we
            // don't leave behind empty brackets on the declaration.
            //
            // Everything written above the attribute is leading trivia of the list, so
            // removing it with KeepNoTrivia took the declaration's doc comment and any
            // `//` comments with it — and cut `#if` / `#region` lines loose from their
            // `#endif` / `#endregion`, which does not compile. Move that trivia onto the
            // declaration instead, and keep whatever sat between the `]` and the
            // declaration (a closing `#endif` lives there). Blank space at the front of
            // that second part is dropped: the list's own trivia already ends with the
            // indentation the declaration needs, so keeping it would leave behind the
            // empty line the attribute used to occupy.
            var stripped = firstOwner.RemoveNode(firstList, SyntaxRemoveOptions.KeepNoTrivia)!;
            var leading = firstList
                .GetLeadingTrivia()
                .AddRange(stripped.GetLeadingTrivia().SkipWhile(IsBlank));
            newRoot = root.ReplaceNode(firstOwner, stripped.WithLeadingTrivia(leading));
        }
        else
        {
            newRoot = root.RemoveNode(attribute, SyntaxRemoveOptions.KeepExteriorTrivia)!;
        }

        return document.WithSyntaxRoot(newRoot);
    }

    // A list that is not the first on its declaration — the record twin
    // `[Id("User")][property: Id("User")] Guid? PerformedById`, or `[Obsolete]` then `[Id(...)]`
    // on separate lines. Everything written above the declaration belongs to the first list, so
    // the declaration's own leading trivia is left alone: rebuilding it, as the first-list path
    // does, dropped the indentation and any blank line above the member. Only this list and its
    // trivia go, keeping two things:
    //  * comments or directives written above it, which move onto the next token;
    //  * the space before the next token when this list sat flush against the previous one,
    //    `[A][B] T x`, since that space was B's trailing trivia.
    static SyntaxNode RemoveLaterList(SyntaxNode root, AttributeListSyntax list)
    {
        var previous = list.OpenBracketToken.GetPreviousToken();
        var next = list.CloseBracketToken.GetNextToken();

        var leading = list.GetLeadingTrivia();
        var nextLeading = next.LeadingTrivia;
        if (leading.Any(_ => !IsBlank(_)))
        {
            nextLeading = leading.AddRange(nextLeading.SkipWhile(IsBlank));
        }

        if (previous.TrailingTrivia.Count == 0 &&
            nextLeading.Count == 0)
        {
            nextLeading = TriviaList(Space);
        }

        // Mark the next token so it can be found again once the list is gone. An annotation
        // does not change the text, so the list's span still locates it in the marked tree.
        var annotation = new SyntaxAnnotation();
        var marked = root.ReplaceToken(next, next.WithAdditionalAnnotations(annotation));
        var markedList = marked
            .FindNode(list.Span)
            .FirstAncestorOrSelf<AttributeListSyntax>()!;
        var removed = marked.RemoveNode(markedList, SyntaxRemoveOptions.KeepNoTrivia)!;
        var markedNext = removed.GetAnnotatedTokens(annotation).Single();
        return removed.ReplaceToken(markedNext, markedNext.WithLeadingTrivia(nextLeading));
    }

    static bool IsBlank(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.WhitespaceTrivia) ||
        trivia.IsKind(SyntaxKind.EndOfLineTrivia);
}
