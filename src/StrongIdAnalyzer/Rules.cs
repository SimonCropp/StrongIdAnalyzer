// Every diagnostic the analyzer can raise: the descriptors, the property-bag keys the
// code fixes read back, and one Report method per rule.
//
// Analysis code calls `Rules.ReportDropped(...)` rather than assembling a Diagnostic
// inline, so the message arguments, additional-location layout and property bag for a
// given SIA are defined once, next to the descriptor that documents them. The Report
// methods take no view on *whether* to fire — every suppression decision stays in the
// analyzer; these only build and raise.
//
// Each rule reports from exactly one Roslyn context type, so the methods are typed to
// that context (the three context types share no common interface).
static class Rules
{
    public const string ValueKey = "IdValue";

    // SIA001 emits both sides' tags so the fixer can offer a fix for either side:
    // TargetValueKey = tag to apply if the user fixes the target (= source's first tag).
    // SourceValueKey = tag to apply if the user fixes the source (= target's first tag).
    public const string TargetValueKey = "IdValueTarget";
    public const string SourceValueKey = "IdValueSource";

    // Messages name both symbols and spell out the fix. Build output (and anything that
    // reads it — CI logs, AI agents) only ever sees the message string, so it has to
    // carry enough for a reader to act without opening an IDE: which declaration is
    // wrong, what to put on it, and where that declaration lives.
    const string helpRoot = "https://github.com/SimonCropp/StrongIdAnalyzer/blob/main/docs/";

    static readonly DiagnosticDescriptor idMismatch = new(
        id: "SIA001",
        title: "Id type mismatch",
        messageFormat: "{0} is {1} and {2} {3}, which is {4}. {5}.",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A value tagged with one domain id flows into a target tagged with a different domain id. Either the target's [Id] is wrong, or the wrong value is being passed. Change whichever side is mistaken so both carry the same id.",
        helpLinkUri: helpRoot + "SIA001.md");

    static readonly DiagnosticDescriptor missingSourceId = new(
        id: "SIA002",
        title: "Source has no Id while target requires one",
        messageFormat: "{0} has no [Id] but {1} {2}, which is {3}. Fix: add {4} to {0}{5}.",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An untagged value flows into a target that carries an [Id]. Add a matching [Id] to the source declaration so the analyzer can track the value, or rename it to follow the Id / XxxId convention. Apply mechanically with: dotnet format analyzers --diagnostics SIA002.",
        helpLinkUri: helpRoot + "SIA002.md");

    static readonly DiagnosticDescriptor droppedId = new(
        id: "SIA003",
        title: "Source has Id while target has none",
        messageFormat: "{0} is {1} but {2} {3}, which has no [Id]. Fix: add {4} to {3}{5}.",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A tagged value flows into a target that carries no [Id], so the domain id is lost from that point on. Add a matching [Id] to the target declaration, or rename it to follow the Id / XxxId convention. Apply mechanically with: dotnet format analyzers --diagnostics SIA003.",
        helpLinkUri: helpRoot + "SIA003.md");

    static readonly DiagnosticDescriptor ambiguousConvention = new(
        id: "SIA004",
        title: "Ambiguous conventional Id name",
        messageFormat: "{0} and {1} both infer the conventional Id name \"{2}\" from their declaring type name. Fix: add an explicit [Id(\"...\")] with a distinct value to at least one of them.",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Two types with the same unqualified name each declare a member named Id, so the naming convention would give both members the same domain id. Add an explicit [Id(\"...\")] to at least one of the members to disambiguate.",
        helpLinkUri: helpRoot + "SIA004.md",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor redundantId = new(
        id: "SIA005",
        title: "Redundant [Id] attribute",
        messageFormat: "[Id(\"{0}\")] on {1} is redundant: the naming convention already infers \"{0}\". Fix: remove the attribute.",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The explicit [Id] repeats exactly what the naming convention infers from the member and type names. Remove the attribute. Apply mechanically with: dotnet format analyzers --diagnostics SIA005.",
        helpLinkUri: helpRoot + "SIA005.md",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor singletonUnion = new(
        id: "SIA006",
        title: "[UnionId] with a single option should be [Id]",
        messageFormat: "[UnionId(\"{0}\")] on {1} has only one option. Fix: replace it with [Id(\"{0}\")].",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "[UnionId] expresses a choice between several domain ids; with a single option it is just [Id]. Replace it. Apply mechanically with: dotnet format analyzers --diagnostics SIA006.",
        helpLinkUri: helpRoot + "SIA006.md");

    static readonly DiagnosticDescriptor emptyTag = new(
        id: "SIA007",
        title: "Id tag must not be empty or whitespace",
        messageFormat: "[{0}] on {1} has an empty tag. Fix: supply a non-empty domain name, e.g. [{0}(\"Customer\")].",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An [Id] or [UnionId] tag that is empty or whitespace cannot identify a domain. Supply the name of the domain type the value identifies.",
        helpLinkUri: helpRoot + "SIA007.md");

    public static readonly ImmutableArray<DiagnosticDescriptor> All =
        [idMismatch, missingSourceId, droppedId, ambiguousConvention, redundantId, singletonUnion, emptyTag];

    // SIA001. Both declarations ride along as additional locations so the code fix can
    // offer to fix either side — slot 0 is always the target, slot 1 the source. Slots
    // without a user-owned declaration (library refs, locals) hold Location.None as a
    // sentinel so the indexes stay stable; the fixer checks IsInSource before offering.
    //
    // `sourceSymbol` / `targetSymbol` are what the message names; `sourceFixSite` /
    // `targetFixSite` are the declarations the fixer may edit (null when the tag came
    // from a wrapper type rather than a declaration).
    public static void ReportMismatch(
        OperationAnalysisContext context,
        Location location,
        ISymbol? sourceSymbol,
        ISymbol? sourceFixSite,
        IdInfo source,
        ISymbol? targetSymbol,
        ISymbol? targetFixSite,
        IdInfo target,
        string relation = "flows to") =>
        context.ReportDiagnostic(Diagnostic.Create(
            idMismatch,
            location,
            additionalLocations:
            [
                targetFixSite.ResolveDeclarationLocation(),
                sourceFixSite.ResolveDeclarationLocation()
            ],
            properties: ImmutableDictionary<string, string?>.Empty
                // Kept for backward-compat: older fixer versions read IdValue and apply
                // it to AdditionalLocations[0] (the target). New fixer prefers the typed keys.
                .Add(ValueKey, source.FirstValue)
                .Add(TargetValueKey, source.FirstValue)
                .Add(SourceValueKey, target.FirstValue),
            messageArgs:
            [
                Describe(sourceSymbol),
                FormatAttribute(source.Tags),
                relation,
                Describe(targetSymbol),
                FormatAttribute(target.Tags),
                MismatchFix(location, sourceSymbol, sourceFixSite, source, targetSymbol, targetFixSite, target)
            ]));

    // The fix clause prefers retagging the target (matches the fixer's default), falls
    // back to the source when only that side is editable, and otherwise can only ask
    // for a different value.
    static string MismatchFix(
        Location location,
        ISymbol? sourceSymbol,
        ISymbol? sourceFixSite,
        IdInfo source,
        ISymbol? targetSymbol,
        ISymbol? targetFixSite,
        IdInfo target)
    {
        if (targetFixSite.IsEditable())
        {
            return $"Fix: apply {FormatAttribute(source.Tags)} to {Describe(targetSymbol)}{Site(location, targetFixSite)}, or pass a value tagged {FormatAttribute(target.Tags)}";
        }

        if (sourceFixSite.IsEditable())
        {
            return $"Fix: apply {FormatAttribute(target.Tags)} to {Describe(sourceSymbol)}{Site(location, sourceFixSite)}, or pass a value tagged {FormatAttribute(target.Tags)}";
        }

        return $"Fix: pass a value tagged {FormatAttribute(target.Tags)}";
    }

    // SIA002. `fixTarget` is the untagged side's declaration — the codefix adds an [Id]
    // there matching `info`, which is the tagged side's info. `taggedSymbol` is only
    // named in the message.
    public static void ReportMissingSource(
        OperationAnalysisContext context,
        Location location,
        ISymbol fixTarget,
        ISymbol? taggedSymbol,
        IdInfo info,
        string relation = "flows to") =>
        context.ReportDiagnostic(CreateFixable(
            missingSourceId,
            location,
            fixTarget,
            info,
            messageArgs: (fixTags, displayTags) =>
            [
                Describe(fixTarget),
                relation,
                Describe(taggedSymbol),
                displayTags,
                FormatAttribute(fixTags),
                Site(location, fixTarget)
            ]));

    // SIA003. Mirror of SIA002: `fixTarget` is the untagged target's declaration and
    // `info` the tagged source's info.
    public static void ReportDropped(
        OperationAnalysisContext context,
        Location location,
        ISymbol? taggedSymbol,
        IdInfo info,
        ISymbol fixTarget) =>
        context.ReportDiagnostic(CreateFixable(
            droppedId,
            location,
            fixTarget,
            info,
            messageArgs: (fixTags, displayTags) =>
            [
                Describe(taggedSymbol),
                displayTags,
                "flows to",
                Describe(fixTarget),
                FormatAttribute(fixTags),
                Site(location, fixTarget)
            ]));

    // SIA004. Compilation-end: several declarations infer the same conventional name.
    // `others` are the colliding declarations, named in the message so the reader can
    // find the counterpart without a second search.
    public static void ReportAmbiguousConvention(
        CompilationAnalysisContext context,
        Location location,
        ISymbol symbol,
        IEnumerable<ISymbol> others,
        string conventionName) =>
        context.ReportDiagnostic(Diagnostic.Create(
            ambiguousConvention,
            location,
            Describe(symbol, qualifiedFormat),
            string.Join(", ", others.Select(_ => Describe(_, qualifiedFormat))),
            conventionName));

    // SIA005. Compilation-end: an explicit [Id] repeating what convention already infers.
    public static void ReportRedundant(
        CompilationAnalysisContext context,
        Location location,
        ISymbol symbol,
        string value) =>
        context.ReportDiagnostic(Diagnostic.Create(redundantId, location, value, Describe(symbol)));

    // SIA006. The single option travels in the property bag so the fixer can rewrite
    // [UnionId("X")] to [Id("X")] without re-parsing the attribute.
    public static void ReportSingletonUnion(
        SymbolAnalysisContext context,
        Location location,
        string singleValue) =>
        context.ReportDiagnostic(Diagnostic.Create(
            singletonUnion,
            location,
            properties: ImmutableDictionary<string, string?>.Empty.Add(ValueKey, singleValue),
            messageArgs: [singleValue, Describe(context.Symbol)]));

    // SIA007. No codefix — an empty tag doesn't say what the user meant.
    public static void ReportEmptyTag(
        SymbolAnalysisContext context,
        Location location,
        string attributeName) =>
        context.ReportDiagnostic(Diagnostic.Create(emptyTag, location, attributeName, Describe(context.Symbol)));

    // Shared shape for SIA002/SIA003: the diagnostic carries the tags to apply plus the
    // declaration to apply them to.
    static Diagnostic CreateFixable(
        DiagnosticDescriptor rule,
        Location location,
        ISymbol? fixTarget,
        IdInfo info,
        Func<ImmutableArray<string>, string, object[]> messageArgs)
    {
        // Pipe-delimited so a UnionId source can drive multiple codefix options (one
        // [Id(x)] per tag + one combined [UnionId(...)]). Pipe is safe because tag
        // values are identifier-like.
        //
        // When the tagged side carries any explicit [Id]/[UnionId] tags we offer ONLY
        // those as fix suggestions — convention-derived tags (member name, receiver
        // type) on the same side are inferences, not declarations, and proposing them
        // as add-fixes would override the deliberate annotation that's already there.
        // The diagnostic message still shows the full effective tag set so the reader
        // sees what the analyzer matched against, but its Fix clause spells out the
        // same attribute the fixer would write.
        var fixTags = info.ExplicitTags.IsDefaultOrEmpty ? info.Tags : info.ExplicitTags;
        var joined = fixTags.IsDefaultOrEmpty ? "" : string.Join("|", fixTags);
        return Diagnostic.Create(
            rule,
            location,
            additionalLocations: GetAdditionalLocations(fixTarget),
            properties: ImmutableDictionary<string, string?>.Empty.Add(ValueKey, joined),
            messageArgs: messageArgs(fixTags, FormatAttribute(info.Tags)));
    }

    // The attribute the reader should write: one tag → [Id("X")], several → the
    // equivalent [UnionId("X", "Y")].
    static string FormatAttribute(ImmutableArray<string> tags)
    {
        if (tags.IsDefaultOrEmpty)
        {
            return "[Id(\"...\")]";
        }

        if (tags.Length == 1)
        {
            return $"[Id(\"{tags[0]}\")]";
        }

        return $"[UnionId({string.Join(", ", tags.Select(_ => $"\"{_}\""))})]";
    }

    static readonly SymbolDisplayFormat memberFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    // SIA004 is precisely the case where two members share an unqualified name, so it
    // needs the namespace to tell them apart.
    static readonly SymbolDisplayFormat qualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    // Human-readable name for a symbol in a message: `property 'Order.CustomerId'`,
    // `parameter 'orderId' of 'OrderService.Place'`, `return value of 'Repo.Load'`.
    // Null (a wrapper-typed expression, a literal) reads as plain `value`.
    static string Describe(ISymbol? symbol) =>
        Describe(symbol, memberFormat);

    static string Describe(ISymbol? symbol, SymbolDisplayFormat format)
    {
        switch (symbol)
        {
            case null:
                return "value";
            case IParameterSymbol parameter:
                return $"parameter '{parameter.Name}' of '{parameter.ContainingSymbol.ToDisplayString(format)}'";
            case IPropertySymbol property:
                return $"property '{property.ToDisplayString(format)}'";
            case IFieldSymbol field:
                return $"field '{field.ToDisplayString(format)}'";
            case IMethodSymbol method:
                return $"return value of '{method.ToDisplayString(format)}'";
            default:
                return $"'{symbol.ToDisplayString(format)}'";
        }
    }

    // Where the fix lands, relative to the diagnostic: ` (line 12)` when the declaration
    // shares the diagnostic's file, ` (D:\src\Order.cs:12)` otherwise, nothing when it
    // has no source location.
    static string Site(Location diagnosticLocation, ISymbol? fixTarget)
    {
        var declaration = fixTarget.ResolveDeclarationLocation();
        if (!declaration.IsInSource)
        {
            return "";
        }

        var span = declaration.GetMappedLineSpan();
        var line = span.StartLinePosition.Line + 1;
        if (declaration.SourceTree == diagnosticLocation.SourceTree ||
            string.IsNullOrEmpty(span.Path))
        {
            return $" (line {line})";
        }

        return $" ({span.Path}:{line})";
    }

    static bool IsEditable(this ISymbol? symbol) =>
        symbol.ResolveDeclarationLocation().IsInSource;

    static Location[]? GetAdditionalLocations(ISymbol? fixTarget)
    {
        var declaration = fixTarget?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            return null;
        }

        return [declaration.ToLocation()];
    }
}
