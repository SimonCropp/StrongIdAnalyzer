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

    static readonly DiagnosticDescriptor idMismatch = new(
        id: "SIA001",
        title: "Id type mismatch",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target with [Id(\"{1}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor missingSourceId = new(
        id: "SIA002",
        title: "Source has no Id while target requires one",
        messageFormat: "Value has no [Id] attribute but is assigned to a target with [Id(\"{0}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor droppedId = new(
        id: "SIA003",
        title: "Source has Id while target has none",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target without an [Id] attribute",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor ambiguousConvention = new(
        id: "SIA004",
        title: "Ambiguous conventional Id name",
        messageFormat: "Multiple declarations map to the conventional Id name \"{0}\"; add an explicit [Id(\"...\")] to at least one to disambiguate",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor redundantId = new(
        id: "SIA005",
        title: "Redundant [Id] attribute",
        messageFormat: "[Id(\"{0}\")] is redundant because the naming convention already infers this value",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor singletonUnion = new(
        id: "SIA006",
        title: "[UnionId] with a single option should be [Id]",
        messageFormat: "[UnionId(\"{0}\")] has only one option; use [Id(\"{0}\")] instead",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor emptyTag = new(
        id: "SIA007",
        title: "Id tag must not be empty or whitespace",
        messageFormat: "[{0}] tag must not be empty or whitespace",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly ImmutableArray<DiagnosticDescriptor> All =
        [idMismatch, missingSourceId, droppedId, ambiguousConvention, redundantId, singletonUnion, emptyTag];

    // SIA001. Both declarations ride along as additional locations so the code fix can
    // offer to fix either side — slot 0 is always the target, slot 1 the source. Slots
    // without a user-owned declaration (library refs, locals) hold Location.None as a
    // sentinel so the indexes stay stable; the fixer checks IsInSource before offering.
    public static void ReportMismatch(
        OperationAnalysisContext context,
        Location location,
        ISymbol? sourceSymbol,
        IdInfo source,
        ISymbol? targetSymbol,
        IdInfo target) =>
        context.ReportDiagnostic(Diagnostic.Create(
            idMismatch,
            location,
            additionalLocations:
            [
                targetSymbol.ResolveDeclarationLocation(),
                sourceSymbol.ResolveDeclarationLocation()
            ],
            properties: ImmutableDictionary<string, string?>.Empty
                // Kept for backward-compat: older fixer versions read IdValue and apply
                // it to AdditionalLocations[0] (the target). New fixer prefers the typed keys.
                .Add(ValueKey, source.FirstValue)
                .Add(TargetValueKey, source.FirstValue)
                .Add(SourceValueKey, target.FirstValue),
            messageArgs: [source.Format(), target.Format()]));

    // SIA002. `fixTarget` is the untagged side's declaration — the codefix adds an [Id]
    // there matching `info`, which is the tagged side's info.
    public static void ReportMissingSource(
        OperationAnalysisContext context,
        Location location,
        ISymbol fixTarget,
        IdInfo info) =>
        context.ReportDiagnostic(CreateFixable(missingSourceId, location, fixTarget, info));

    // SIA003. Mirror of SIA002: `fixTarget` is the untagged target's declaration and
    // `info` the tagged source's info.
    public static void ReportDropped(
        OperationAnalysisContext context,
        Location location,
        ISymbol fixTarget,
        IdInfo info) =>
        context.ReportDiagnostic(CreateFixable(droppedId, location, fixTarget, info));

    // SIA004. Compilation-end: several declarations infer the same conventional name.
    public static void ReportAmbiguousConvention(
        CompilationAnalysisContext context,
        Location location,
        string conventionName) =>
        context.ReportDiagnostic(Diagnostic.Create(ambiguousConvention, location, conventionName));

    // SIA005. Compilation-end: an explicit [Id] repeating what convention already infers.
    public static void ReportRedundant(
        CompilationAnalysisContext context,
        Location location,
        string value) =>
        context.ReportDiagnostic(Diagnostic.Create(redundantId, location, value));

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
            messageArgs: singleValue));

    // SIA007. No codefix — an empty tag doesn't say what the user meant.
    public static void ReportEmptyTag(
        SymbolAnalysisContext context,
        Location location,
        string attributeName) =>
        context.ReportDiagnostic(Diagnostic.Create(emptyTag, location, attributeName));

    // Shared shape for SIA002/SIA003: the diagnostic carries the tags to apply plus the
    // declaration to apply them to.
    static Diagnostic CreateFixable(
        DiagnosticDescriptor rule,
        Location location,
        ISymbol? fixTarget,
        IdInfo info)
    {
        // Pipe-delimited so a UnionId source can drive multiple codefix options (one
        // [Id(x)] per tag + one combined [UnionId(...)]). Pipe is the same separator
        // used in the rendered message — safe because tag values are identifier-like.
        //
        // When the tagged side carries any explicit [Id]/[UnionId] tags we offer ONLY
        // those as fix suggestions — convention-derived tags (member name, receiver
        // type) on the same side are inferences, not declarations, and proposing them
        // as add-fixes would override the deliberate annotation that's already there.
        // The diagnostic message still shows the full effective tag set so the reader
        // sees what the analyzer matched against.
        var fixTags = info.ExplicitTags.IsDefaultOrEmpty ? info.Tags : info.ExplicitTags;
        var joined = fixTags.IsDefaultOrEmpty ? "" : string.Join("|", fixTags);
        var displayJoined = info.Tags.IsDefaultOrEmpty ? "" : string.Join("|", info.Tags);
        return Diagnostic.Create(
            rule,
            location,
            additionalLocations: GetAdditionalLocations(fixTarget),
            properties: ImmutableDictionary<string, string?>.Empty.Add(ValueKey, joined),
            messageArgs: displayJoined);
    }

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
