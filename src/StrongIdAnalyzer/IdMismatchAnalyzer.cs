namespace StrongIdAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IdMismatchAnalyzer : DiagnosticAnalyzer
{
    public const string ValueKey = "IdValue";

    // SIA001 emits both sides' tags so the fixer can offer a fix for either side:
    // TargetValueKey = tag to apply if the user fixes the target (= source's first tag).
    // SourceValueKey = tag to apply if the user fixes the source (= target's first tag).
    public const string TargetValueKey = "IdValueTarget";
    public const string SourceValueKey = "IdValueSource";

    static readonly DiagnosticDescriptor idMismatchRule = new(
        id: "SIA001",
        title: "Id type mismatch",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target with [Id(\"{1}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor missingSourceIdRule = new(
        id: "SIA002",
        title: "Source has no Id while target requires one",
        messageFormat: "Value has no [Id] attribute but is assigned to a target with [Id(\"{0}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor droppedIdRule = new(
        id: "SIA003",
        title: "Source has Id while target has none",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target without an [Id] attribute",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor ambiguousConventionRule = new(
        id: "SIA004",
        title: "Ambiguous conventional Id name",
        messageFormat: "Multiple declarations map to the conventional Id name \"{0}\"; add an explicit [Id(\"...\")] to at least one to disambiguate",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    static readonly DiagnosticDescriptor redundantIdRule = new(
        id: "SIA005",
        title: "Redundant [Id] attribute",
        messageFormat: "[Id(\"{0}\")] is redundant because the naming convention already infers this value",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor singletonUnionRule = new(
        id: "SIA006",
        title: "[UnionId] with a single option should be [Id]",
        messageFormat: "[UnionId(\"{0}\")] has only one option; use [Id(\"{0}\")] instead",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor emptyTagRule = new(
        id: "SIA007",
        title: "Id tag must not be empty or whitespace",
        messageFormat: "[{0}] tag must not be empty or whitespace",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [idMismatchRule, missingSourceIdRule, droppedIdRule, ambiguousConventionRule, redundantIdRule, singletonUnionRule, emptyTagRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            // IdAttribute and UnionIdAttribute are source-generated as internal per
            // assembly, so the same metadata name can resolve to distinct symbols across
            // compilations (and Compilation.GetTypeByMetadataName returns null under
            // ambiguity). Matching attributes by fully-qualified name instead of symbol
            // identity keeps cross-assembly usage working — e.g. messages assembly tags
            // a property with [Id("Customer")] and the consumer assembly assigns it.
            var suppressedNamespaces = NamespaceSuppression.Read(
                start.Options.AnalyzerConfigOptionsProvider,
                start.Compilation);
            var config = new Config(suppressedNamespaces, start.Compilation);

            start.RegisterOperationAction(
                _ => AnalyzeArgument(_, config),
                OperationKind.Argument);
            start.RegisterOperationAction(
                _ => AnalyzeSimpleAssignment(_, config),
                OperationKind.SimpleAssignment);
            start.RegisterOperationAction(
                _ => AnalyzePropertyInitializer(_, config),
                OperationKind.PropertyInitializer);
            start.RegisterOperationAction(
                _ => AnalyzeFieldInitializer(_, config),
                OperationKind.FieldInitializer);
            start.RegisterOperationAction(
                _ => AnalyzeBinaryOperator(_, config),
                OperationKind.Binary);
            start.RegisterOperationAction(
                _ => AnalyzeLoop(_, config),
                OperationKind.Loop);

            // Track convention-derived Id names across the whole compilation so we can
            // report ambiguity (SIA004) and redundant [Id] attributes (SIA005) at end.
            // Only the `Id`-property-on-type rule feeds the ambiguity map — the `XxxId`
            // rule never participates because property-name collisions across types are
            // the intended matching behavior (e.g. `Order.CustomerId` and `Invoice.CustomerId`
            // both referring to the same Customer tag).
            var ambiguity = new ConcurrentDictionary<
                string,
                ConcurrentBag<ISymbol>>(StringComparer.Ordinal);
            var redundantCandidates = new ConcurrentBag<
                (ISymbol Symbol, string Value, SyntaxReference Reference)>();

            start.RegisterSymbolAction(
                _ => CollectConvention(_, ambiguity, redundantCandidates),
                SymbolKind.Property,
                SymbolKind.Field,
                SymbolKind.Parameter);

            start.RegisterSymbolAction(
                AnalyzeSingletonUnion,
                SymbolKind.Property,
                SymbolKind.Field,
                SymbolKind.Parameter);

            start.RegisterSymbolAction(
                AnalyzeEmptyTag,
                SymbolKind.Property,
                SymbolKind.Field,
                SymbolKind.Parameter,
                SymbolKind.Method);

            start.RegisterCompilationEndAction(
                _ => ReportConventionDiagnostics(_, ambiguity, redundantCandidates));
        });
    }

    // SIA007: `[Id("")]`, `[Id(" ")]`, `[UnionId("")]`, `[UnionId("", "Customer")]`,
    // and `[UnionId()]` are all semantically meaningless — an empty/whitespace tag
    // can never identify a domain type. Fires at Error severity so the build
    // breaks; there is no codefix because the user's intent isn't recoverable
    // from an empty string.
    static void AnalyzeEmptyTag(SymbolAnalysisContext context)
    {
        foreach (var attribute in context.Symbol.GetAttributes())
        {
            CheckEmptyTag(context, attribute);
        }

        if (context.Symbol is IMethodSymbol method)
        {
            foreach (var attribute in method.GetReturnTypeAttributes())
            {
                CheckEmptyTag(context, attribute);
            }
        }
    }

    static void CheckEmptyTag(SymbolAnalysisContext context, AttributeData attribute)
    {
        if (IsAttributeNamed(attribute, idMetadataName))
        {
            // Generic `[Id<T>]` can't have an empty tag — the type argument binds
            // at compile time. Only the string-arg constructor needs checking.
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string s &&
                string.IsNullOrWhiteSpace(s))
            {
                ReportEmptyTag(context, attribute, "Id");
            }
            return;
        }

        if (!IsAttributeNamed(attribute, unionIdMetadataName))
        {
            return;
        }

        if (attribute.ConstructorArguments.Length == 0)
        {
            ReportEmptyTag(context, attribute, "UnionId");
            return;
        }

        var first = attribute.ConstructorArguments[0];
        if (first.Kind != TypedConstantKind.Array)
        {
            return;
        }

        if (first.Values.Length == 0)
        {
            ReportEmptyTag(context, attribute, "UnionId");
            return;
        }

        foreach (var element in first.Values)
        {
            if (element.Value is string option &&
                string.IsNullOrWhiteSpace(option))
            {
                ReportEmptyTag(context, attribute, "UnionId");
                return;
            }
        }
    }

    static void ReportEmptyTag(SymbolAnalysisContext context, AttributeData attribute, string attributeName)
    {
        var reference = attribute.ApplicationSyntaxReference;
        if (reference is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            emptyTagRule,
            Location.Create(reference.SyntaxTree, reference.Span),
            attributeName));
    }

    static void AnalyzeSingletonUnion(SymbolAnalysisContext context)
    {
        foreach (var attribute in context.Symbol.GetAttributes())
        {
            if (!IsAttributeNamed(attribute, unionIdMetadataName))
            {
                continue;
            }

            var options = ExtractUnionOptions(attribute);
            // Only the exact singleton case is redundant. Empty `[UnionId()]`
            // is a different shape of user error — "has only one option" would
            // lie, and the codefix would emit `[Id("")]`, which is worse than
            // staying silent.
            if (options.Length != 1)
            {
                return;
            }

            var reference = attribute.ApplicationSyntaxReference;
            if (reference is null)
            {
                return;
            }

            var singleValue = options[0];
            var properties = ImmutableDictionary<string, string?>.Empty.Add(ValueKey, singleValue);
            context.ReportDiagnostic(Diagnostic.Create(
                singletonUnionRule,
                Location.Create(reference.SyntaxTree, reference.Span),
                properties: properties,
                messageArgs: singleValue));
            return;
        }
    }

    static void CollectConvention(
        SymbolAnalysisContext context,
        ConcurrentDictionary<string, ConcurrentBag<ISymbol>> ambiguity,
        ConcurrentBag<(ISymbol Symbol, string Value, SyntaxReference Reference)> redundantCandidates)
    {
        var symbol = context.Symbol;
        if (symbol.DeclaringSyntaxReferences.IsEmpty)
        {
            return;
        }

        if (!TryGetConventionName(symbol, out var conventionName, out var fromContainingType))
        {
            return;
        }

        // Multi-declarator fields (`[Id("Customer")] Guid CustomerId, OrderId;`)
        // share the attribute across all declarators. SIA005 would fire only on
        // the declarator whose convention matches the tag, and removing the
        // shared attribute silently changes what the unflagged declarator means
        // — OrderId would flip from the explicit "Customer" tag to its
        // convention-inferred "Order". Skip the whole group rather than produce
        // a fix that breaks its sibling.
        if (symbol is IFieldSymbol &&
            symbol.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken)
                is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: > 1 } })
        {
            return;
        }

        var explicitAttribute = GetExplicitIdAttribute(symbol);
        var hasAnyIdFamily = HasAnyIdFamilyAttribute(symbol);

        // Only the containing-type-named rule (`public Guid Id`) feeds ambiguity tracking.
        // Any explicit Id-family attribute ([Id] or [UnionId]) opts out — it resolves the
        // ambiguity that SIA004 would otherwise complain about.
        if (fromContainingType && !hasAnyIdFamily)
        {
            ambiguity
                .GetOrAdd(conventionName, _ => [])
                .Add(symbol);
        }

        if (explicitAttribute is null)
        {
            return;
        }

        var explicitValue = GetAttributeValue(explicitAttribute);
        if (!string.Equals(explicitValue, conventionName, StringComparison.Ordinal))
        {
            return;
        }

        var syntaxRef = explicitAttribute.ApplicationSyntaxReference;
        if (syntaxRef is null)
        {
            return;
        }

        redundantCandidates.Add((symbol, conventionName, syntaxRef));
    }

    static void ReportConventionDiagnostics(
        CompilationAnalysisContext context,
        ConcurrentDictionary<string, ConcurrentBag<ISymbol>> ambiguity,
        ConcurrentBag<(ISymbol Symbol, string Value, SyntaxReference Reference)> redundantCandidates)
    {
        foreach (var entry in ambiguity)
        {
            var distinct = entry.Value
                .Distinct(SymbolEqualityComparer.Default)
                .ToArray();
            if (distinct.Length < 2)
            {
                continue;
            }

            // Two `Id` properties on the same unqualified type name but different
            // original containing types collide. If all entries share one containing
            // type (partials / duplicate notifications), there is no conflict.
            var distinctTypes = distinct
                .Select(_ => (ISymbol?)_.ContainingType?.OriginalDefinition)
                .Where(_ => _ is not null)
                .Select(_ => _!)
                .Distinct(SymbolEqualityComparer.Default)
                .Count();
            if (distinctTypes < 2)
            {
                continue;
            }

            foreach (var symbol in distinct)
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        ambiguousConventionRule,
                        Location.Create(reference.SyntaxTree, reference.Span),
                        entry.Key));
                }
            }
        }

        // Dedupe redundant candidates by (symbol, attribute location) — RegisterSymbolAction
        // only fires once per symbol per compilation, but we defensively dedupe in case of
        // partial-declaration edge cases.
        var seen = new HashSet<(ISymbol Symbol, int Position)>(
            new RedundantKeyComparer());
        foreach (var candidate in redundantCandidates)
        {
            if (!seen.Add((candidate.Symbol, candidate.Reference.Span.Start)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                redundantIdRule,
                Location.Create(candidate.Reference.SyntaxTree, candidate.Reference.Span),
                candidate.Value));
        }
    }

    sealed class RedundantKeyComparer : IEqualityComparer<(ISymbol Symbol, int Position)>
    {
        public bool Equals((ISymbol Symbol, int Position) x, (ISymbol Symbol, int Position) y) =>
            x.Position == y.Position &&
            SymbolEqualityComparer.Default.Equals(x.Symbol, y.Symbol);

        public int GetHashCode((ISymbol Symbol, int Position) obj) =>
            unchecked(SymbolEqualityComparer.Default.GetHashCode(obj.Symbol) * 397 ^ obj.Position);
    }

    // Convention mapping:
    //   - property/field named "Id"       -> containing type's metadata name (ignoring namespace and parent)
    //   - property/field/parameter named "<Xxx>Id" -> "<Xxx>" (first char uppercased, so
    //     camelCase parameters like `orderId` match the PascalCase tag of `OrderId`)
    // The `Id` rule does not apply to parameters — a bare `id` has no containing-type
    // equivalent, and this keeps method signatures' inferred tags purely name-driven.
    // `fromContainingType` is true only when the `Id` rule fired; that's the one that can
    // produce cross-type ambiguity (two distinct `Ns1.Order` and `Ns2.Order` both claiming
    // "Order"). The `<Xxx>Id` rule uses only the identifier name and is unambiguous by
    // design.
    static bool TryGetConventionName(ISymbol symbol, out string conventionName, out bool fromContainingType)
    {
        conventionName = "";
        fromContainingType = false;

        string name;
        INamedTypeSymbol? containingType;
        var allowIdRule = true;

        switch (symbol)
        {
            case IPropertySymbol property:
                if (property.IsIndexer)
                {
                    return false;
                }

                name = property.Name;
                containingType = property.ContainingType;

                // Anonymous-type properties can't carry [Id]. The `Id` rule (containing-type
                // name) on an anon type would map to the synthesized `<>f__AnonymousType*`
                // name, which is meaningless as a tag — disable it. The `XxxId` rule is
                // purely name-based, so it still applies and lets values read OUT of an anon
                // property carry their conventional tag (e.g. `extract.CustomerId` flowing
                // into a `[Id("Customer")]` target). Writes INTO anon properties are
                // separately silenced in Report — no fix site exists on the anon side.
                if (containingType is { IsAnonymousType: true })
                {
                    allowIdRule = false;
                }

                break;
            case IFieldSymbol field:
                if (field.IsImplicitlyDeclared)
                {
                    return false;
                }

                name = field.Name;
                containingType = field.ContainingType;
                break;
            case IParameterSymbol parameter:
                name = parameter.Name;
                containingType = null;
                allowIdRule = false;
                break;
            default:
                return false;
        }

        if (allowIdRule && name == "Id")
        {
            if (containingType is null || containingType.Name.Length == 0)
            {
                return false;
            }

            conventionName = containingType.Name;
            fromContainingType = true;
            return true;
        }

        if (name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal))
        {
            var prefix = name.Substring(0, name.Length - 2);
            if (char.IsLower(prefix[0]))
            {
                prefix = char.ToUpperInvariant(prefix[0]) + prefix.Substring(1);
            }

            conventionName = prefix;
            return true;
        }

        return false;
    }

    static bool TryGetConventionName(ISymbol symbol, out string conventionName) =>
        TryGetConventionName(symbol, out conventionName, out _);

    // Returns the symbol's [Id] attribute specifically — not [UnionId]. Used by the
    // SIA005 "redundant" check, which only applies to single-tag [Id("X")] values that
    // happen to equal what the convention would infer.
    static AttributeData? GetExplicitIdAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (IsAnyIdAttribute(attribute))
            {
                return attribute;
            }
        }

        return null;
    }

    // True when the symbol carries any Id-family attribute — [Id] or [UnionId]. Used by
    // SIA004 ambiguity tracking so explicitly-tagged declarations drop out of the pool
    // that convention alone would have collided.
    static bool HasAnyIdFamilyAttribute(ISymbol symbol)
    {
        if (HasIdFamilyAttribute(symbol.GetAttributes()))
        {
            return true;
        }

        // A record primary-ctor parameter with [Id] / [UnionId] counts for the
        // synthesized property too — the attribute is physically on the parameter
        // (its default target) but the user means it to apply to both.
        if (symbol is IPropertySymbol property &&
            FindRecordPrimaryParameter(property) is { } parameter)
        {
            return HasIdFamilyAttribute(parameter.GetAttributes());
        }

        return false;
    }

    static bool HasIdFamilyAttribute(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (IsAnyIdAttribute(attribute) ||
                IsAnyUnionIdAttribute(attribute))
            {
                return true;
            }
        }

        return false;
    }

    // Records: a property synthesized from a primary-ctor parameter carries the
    // parameter's [Id] / [UnionId] (the compiler leaves such attributes on the
    // parameter, which is their default target). Returns the parameter so callers
    // can read its attributes as if they were on the property.
    static IParameterSymbol? FindRecordPrimaryParameter(IPropertySymbol property)
    {
        var type = property.ContainingType;
        if (type is null || !type.IsRecord)
        {
            return null;
        }

        foreach (var constructor in type.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.Name == property.Name &&
                    SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type))
                {
                    return parameter;
                }
            }
        }

        return null;
    }

    static string? GetAttributeValue(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length > 0 &&
            attribute.ConstructorArguments[0].Value is string { Length: > 0 } value)
        {
            return value;
        }

        if (TryGetGenericIdTag(attribute, out var genericTag))
        {
            return genericTag;
        }

        return null;
    }

    const string idMetadataName = "IdAttribute";
    const string unionIdMetadataName = "UnionIdAttribute";
    const string idTagMetadataName = "IdTagAttribute";
    const string indexAttributeMetadataName = "StrongIdIndexAttribute";

    // Looks up pre-resolved tags for `symbol` in the containing-assembly index, if one
    // was shipped. A hit returns the full tag set; callers can skip the usual override /
    // interface / convention walk entirely. Returns false when the symbol is in the
    // source assembly (no cross-assembly walk to skip) or the containing assembly has
    // no index attribute.
    static bool TryGetFromIndex(ISymbol symbol, Config config, out ImmutableArray<string> tags)
    {
        tags = default;
        var assembly = symbol.ContainingAssembly;
        if (assembly is null ||
            SymbolEqualityComparer.Default.Equals(assembly, config.Compilation.Assembly))
        {
            return false;
        }

        // Manual TryGetValue/TryAdd avoids the closure allocation that GetOrAdd(key, lambda)
        // would make per call — Compilation would be captured every time.
        if (!config.IndexCache.TryGetValue(assembly, out var index))
        {
            index = LoadIndex(assembly, config.Compilation);
            config.IndexCache.TryAdd(assembly, index);
        }

        return index is not null && index.TryGetValue(symbol, out tags);
    }

    // Reads [assembly: StrongIdIndex("...")] and pre-resolves every "DocId=Tag1,Tag2;..."
    // entry into an ISymbol → tags map. Parameter DocIds use a custom "M:...::paramName"
    // form since Roslyn has no native parameter DocId. Returns null when the assembly
    // has no index attribute; callers treat that as "fall back to the walk".
    static Dictionary<ISymbol, ImmutableArray<string>>? LoadIndex(IAssemblySymbol assembly, Compilation compilation)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!IsAttributeNamed(attribute, indexAttributeMetadataName))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 0 ||
                attribute.ConstructorArguments[0].Value is not string encoded)
            {
                return null;
            }

            var dict = new Dictionary<ISymbol, ImmutableArray<string>>(SymbolEqualityComparer.Default);
            foreach (var entry in encoded.Split(';'))
            {
                if (entry.Length == 0)
                {
                    continue;
                }

                var eq = entry.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var rawKey = entry.Substring(0, eq);
                var rawValue = entry.Substring(eq + 1);

                var symbol = ResolveIndexSymbol(rawKey, compilation);
                if (symbol is null)
                {
                    continue;
                }

                ImmutableArray<string> tags = rawValue.Length == 0
                    ? []
                    : [..rawValue.Split(',')];
                dict[symbol] = tags;
            }
            return dict;
        }

        return null;
    }

    static ISymbol? ResolveIndexSymbol(string key, Compilation compilation)
    {
        var sep = key.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0)
        {
            return DocumentationCommentId.GetFirstSymbolForDeclarationId(key, compilation);
        }

        var methodId = key.Substring(0, sep);
        var paramName = key.Substring(sep + 2);
        if (DocumentationCommentId.GetFirstSymbolForDeclarationId(methodId, compilation)
            is not IMethodSymbol method)
        {
            return null;
        }

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Name == paramName)
            {
                return parameter;
            }
        }

        return null;
    }
    const string idNamespace = "StrongIdAnalyzer";
    const string idGenericMetadataName = "IdAttribute`1";
    const string unionIdGenericMetadataPrefix = "UnionIdAttribute`";
    const int unionIdMaxGenericArity = 5;

    readonly struct Config(ImmutableArray<NamespacePattern> suppressedNamespaces, Compilation compilation)
    {
        public ImmutableArray<NamespacePattern> SuppressedNamespaces { get; } = suppressedNamespaces;
        public Compilation Compilation { get; } = compilation;

        // Tag-to-ancestor-name cache. Keyed by a tag string; value is the union of base-type
        // and interface names for every type in the compilation whose simple name equals
        // the tag. Computed lazily and shared across all comparisons in the same compilation.
        public ConcurrentDictionary<string, ImmutableArray<string>> AncestorTagCache { get; } =
            new(StringComparer.Ordinal);

        // Per-assembly tag index loaded lazily from [assembly: StrongIdIndex(...)].
        // When a referenced assembly ships an index, per-symbol tag lookups skip the
        // inheritance walk entirely — a hit returns the pre-resolved tag set directly.
        // Null entries mean "this assembly has no index, fall back to the walk".
        public ConcurrentDictionary<IAssemblySymbol, Dictionary<ISymbol, ImmutableArray<string>>?> IndexCache { get; } =
            new(SymbolEqualityComparer.Default);

        // Foreach loop variable → element tags, populated by the loop analysis action and
        // consulted in GetAccessInfo for ILocalReferenceOperation. Separate from the
        // normal symbol resolution path because locals don't support attributes in C#,
        // so the tag is inferred from the collection being iterated.
        public ConcurrentDictionary<ILocalSymbol, ImmutableArray<string>> LocalBindings { get; } =
            new(SymbolEqualityComparer.Default);
    }

    // Matches by comparing the attribute class's short metadata name and walking its
    // containing namespace chain — avoids the string allocation of ToDisplayString.
    // Works across assembly boundaries where each assembly has its own internal copy
    // of the generated attribute.
    static bool IsAttributeNamed(AttributeData attribute, string typeName)
    {
        var attributeClass = attribute.AttributeClass;
        return attributeClass is not null &&
               attributeClass.MetadataName == typeName &&
               IsInIdNamespace(attributeClass.ContainingNamespace);
    }

    // Returns true when `ns` is the single-segment root namespace `StrongIdAnalyzer`.
    static bool IsInIdNamespace(INamespaceSymbol? ns) =>
        ns is { Name: idNamespace, ContainingNamespace.IsGlobalNamespace: true};

    // Matches `[Id<T>]` — the generic counterpart of `[Id("T")]`. Reads the tag from the
    // type argument's short name, mirroring `nameof(T)`. Open/error type arguments and
    // unresolved type parameters are rejected so malformed usages don't leak a tag.
    static bool TryGetGenericIdTag(AttributeData attribute, out string tag)
    {
        tag = "";
        var attributeClass = attribute.AttributeClass;
        if (attributeClass is null || attributeClass.Arity != 1)
        {
            return false;
        }

        var original = attributeClass.OriginalDefinition;
        if (original.MetadataName != idGenericMetadataName ||
            !IsInIdNamespace(original.ContainingNamespace))
        {
            return false;
        }

        var typeArgument = attributeClass.TypeArguments[0];
        if (typeArgument.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
        {
            return false;
        }

        var name = typeArgument.Name;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        tag = name;
        return true;
    }

    static bool IsAnyIdAttribute(AttributeData attribute) =>
        IsAttributeNamed(attribute, idMetadataName) ||
        TryGetGenericIdTag(attribute, out _);

    // Walk the containing-type chain of `symbol` and collect substituted type-argument
    // short names for every original-definition type parameter marked [IdTag]. The
    // attribute is opt-in at the type-parameter declaration, so this produces a tag set
    // only when the author explicitly marked a generic as an Id-tag source.
    //
    // Scope: only wired into the collection-element path (GetExplicitCollectionTags) so
    // `WellKnownId<Customer>.Guids` flows a tag through LINQ chains. Scalar members
    // (method returns, properties, parameters) still need explicit [Id] / [UnionId] —
    // otherwise every factory method inside a `[IdTag]`-annotated type would surface
    // SIA003 against callers storing the result into an untagged field.
    //
    // Skipped when the type argument is still a type parameter (open-generic reference
    // from inside the declaring type itself) or an error type — same guard TryGetGenericIdTag
    // uses, for the same reason: no real tag name is available yet.
    static ImmutableArray<string> GetImplicitTagsFromContainingGenerics(ISymbol symbol)
    {
        ImmutableArray<string>.Builder? builder = null;
        HashSet<string>? seen = null;
        var containing = symbol.ContainingType;
        while (containing is not null)
        {
            var originalParams = containing.OriginalDefinition.TypeParameters;
            var constructedArgs = containing.TypeArguments;
            var count = Math.Min(originalParams.Length, constructedArgs.Length);
            for (var i = 0; i < count; i++)
            {
                if (!HasIdTagAttribute(originalParams[i]))
                {
                    continue;
                }

                var arg = constructedArgs[i];
                if (arg.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
                {
                    continue;
                }

                var name = arg.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                seen ??= new(StringComparer.Ordinal);
                if (!seen.Add(name))
                {
                    continue;
                }

                builder ??= ImmutableArray.CreateBuilder<string>();
                builder.Add(name);
            }

            containing = containing.ContainingType;
        }

        return builder?.ToImmutable() ?? [];
    }

    static bool HasIdTagAttribute(ITypeParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (IsAttributeNamed(attribute, idTagMetadataName))
            {
                return true;
            }
        }

        return false;
    }

    // Matches `[UnionId<T1, T2, ...>]` (arities 2..5). Each type argument contributes its
    // short name as a tag, mirroring `[UnionId(nameof(T1), nameof(T2), ...)]`.
    static bool TryGetGenericUnionIdTags(AttributeData attribute, out ImmutableArray<string> tags)
    {
        tags = [];
        var attributeClass = attribute.AttributeClass;
        if (attributeClass is null)
        {
            return false;
        }

        var arity = attributeClass.Arity;
        if (arity is < 2 or > unionIdMaxGenericArity)
        {
            return false;
        }

        var original = attributeClass.OriginalDefinition;
        if (!IsInIdNamespace(original.ContainingNamespace) ||
            !IsUnionIdGenericMetadataName(original.MetadataName, arity))
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(arity);
        foreach (var typeArgument in attributeClass.TypeArguments)
        {
            if (typeArgument.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
            {
                return false;
            }

            var name = typeArgument.Name;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            builder.Add(name);
        }

        tags = builder.ToImmutable();
        return true;
    }

    static bool IsAnyUnionIdAttribute(AttributeData attribute) =>
        IsAttributeNamed(attribute, unionIdMetadataName) ||
        TryGetGenericUnionIdTags(attribute, out _);

    // Allocation-free equivalent of `metadataName == "UnionIdAttribute`" + arity` for the
    // single-digit arities we support (2..unionIdMaxGenericArity).
    static bool IsUnionIdGenericMetadataName(string metadataName, int arity) =>
        metadataName.Length == unionIdGenericMetadataPrefix.Length + 1 &&
        metadataName[^1] == (char)('0' + arity) &&
        metadataName.StartsWith(unionIdGenericMetadataPrefix, StringComparison.Ordinal);

    // Widens a source tag set to include ancestor type names. For every tag that resolves
    // to one or more types in the compilation, adds the names of all base classes (up to
    // but not including System.Object) and all implemented interfaces. Tags that don't
    // resolve to any type are left as-is — exact-match only, same as before.
    //
    // Source-only widening models covariance: a value tagged `ProgramBill` flows into a
    // target tagged `ProgramBillBase` because the derived id IS a base id. The reverse
    // still fails because the target isn't widened. Equality comparisons widen both sides
    // because equality is symmetric.
    static IdInfo Widen(IdInfo info, Config config)
    {
        if (info.State != IdState.Present ||
            info.Tags.IsDefaultOrEmpty)
        {
            return info;
        }

        ImmutableArray<string>.Builder? additions = null;
        HashSet<string>? seen = null;

        foreach (var tag in info.Tags)
        {
            var ancestors = GetAncestorTags(tag, config);
            if (ancestors.IsDefaultOrEmpty)
            {
                continue;
            }

            if (additions is null)
            {
                additions = ImmutableArray.CreateBuilder<string>();
                seen = new(info.Tags, StringComparer.Ordinal);
            }

            foreach (var ancestor in ancestors)
            {
                if (seen!.Add(ancestor))
                {
                    additions.Add(ancestor);
                }
            }
        }

        if (additions is null || additions.Count == 0)
        {
            return info;
        }

        return IdInfo.Present([.. info.Tags, .. additions]);
    }

    static ImmutableArray<string> GetAncestorTags(string tag, Config config)
    {
        if (config.AncestorTagCache.TryGetValue(tag, out var cached))
        {
            return cached;
        }

        var computed = ComputeAncestorTags(tag, config.Compilation);
        return config.AncestorTagCache.GetOrAdd(tag, computed);
    }

    static ImmutableArray<string> ComputeAncestorTags(string tag, Compilation compilation)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return ImmutableArray<string>.Empty;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in TypeEnumeration.FindByName(compilation, tag))
        {
            var baseType = type.BaseType;
            while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
            {
                if (baseType.Name.Length > 0)
                {
                    result.Add(baseType.Name);
                }

                baseType = baseType.BaseType;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (iface.Name.Length > 0)
                {
                    result.Add(iface.Name);
                }
            }
        }

        if (result.Count == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        return [..result];
    }

    static void AnalyzeBinaryOperator(OperationAnalysisContext context, Config config)
    {
        var operation = (IBinaryOperation)context.Operation;
        if (operation.OperatorKind != BinaryOperatorKind.Equals &&
            operation.OperatorKind != BinaryOperatorKind.NotEquals)
        {
            return;
        }

        var leftSymbol = operation.LeftOperand.GetReferencedSymbol();
        var rightSymbol = operation.RightOperand.GetReferencedSymbol();
        var leftInfo = GetAccessInfo(operation.LeftOperand, config);
        var rightInfo = GetAccessInfo(operation.RightOperand, config);

        // Equality is symmetric: if the two tag sets share any tag, the values could
        // represent the same identity. Only fire SIA001 when the sets are disjoint.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.Present)
        {
            // Equality is symmetric, so widen both sides — a derived id comparing equal
            // to a base id is legitimate (the actual entity might be the derived type).
            var leftWidened = Widen(leftInfo, config);
            var rightWidened = Widen(rightInfo, config);
            if (leftWidened.IntersectsWith(rightWidened))
            {
                return;
            }

            // Equality is symmetric so the left/right → source/target labelling is a
            // convention for the message; the fixer doesn't care which is which, it just
            // needs both declarations and both tags available.
            context.ReportDiagnostic(Diagnostic.Create(
                idMismatchRule,
                operation.Syntax.GetLocation(),
                additionalLocations: GetMismatchLocations(rightSymbol, leftSymbol),
                properties: BuildMismatchProperties(leftInfo.FirstValue, rightInfo.FirstValue),
                messageArgs: [leftInfo.Format(), rightInfo.Format()]));
            return;
        }

        // One side tagged, other is a user-owned member with no tag: SIA002 on the missing
        // side so the fixer can add [Id("<same value>")] to its declaration. When the
        // untagged side is Unknown (literal, Guid.Empty, local var, method call) no
        // diagnostic fires — those are routine and not a bug.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.NotPresent)
        {
            ReportMissingOnBinarySide(context, operation.RightOperand, rightSymbol, leftInfo, config);
            return;
        }

        if (leftInfo.State == IdState.NotPresent &&
            rightInfo.State == IdState.Present)
        {
            ReportMissingOnBinarySide(context, operation.LeftOperand, leftSymbol, rightInfo, config);
        }
    }

    static void ReportMissingOnBinarySide(
        OperationAnalysisContext context,
        IOperation untaggedOperand,
        ISymbol? untaggedSymbol,
        IdInfo taggedInfo,
        Config config)
    {
        // Only fix user-owned declarations. Library members (Guid.Empty etc.) can't carry
        // [Id] so firing here would just be noise.
        if (untaggedSymbol is null ||
            untaggedSymbol.DeclaringSyntaxReferences.IsEmpty)
        {
            return;
        }

        // Anonymous-type members can't carry [Id]; suppress for the same reason as the
        // target-side check in Report.
        if (untaggedSymbol is IPropertySymbol { ContainingType.IsAnonymousType: true })
        {
            return;
        }

        if (NamespaceSuppression.IsSuppressed(untaggedSymbol, config.SuppressedNamespaces))
        {
            return;
        }

        context.ReportDiagnostic(CreateFixableDiagnostic(
            missingSourceIdRule,
            untaggedOperand.Syntax.GetLocation(),
            untaggedSymbol,
            taggedInfo));
    }

    static void AnalyzeArgument(OperationAnalysisContext context, Config config)
    {
        var argument = (IArgumentOperation)context.Operation;
        var parameter = argument.Parameter;
        if (parameter is null)
        {
            return;
        }

        var targetInfo = GetIdWithInheritance(parameter, config);
        var sourceSymbol = argument.Value.GetReferencedSymbol();
        var sourceInfo = GetAccessInfo(argument.Value, config);
        Report(
            context,
            argument.Value.Syntax.GetLocation(),
            sourceSymbol,
            sourceInfo,
            parameter,
            targetInfo,
            config);
    }

    static void AnalyzeSimpleAssignment(OperationAnalysisContext context, Config config)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        var targetSymbol = assignment.Target.GetReferencedSymbol();
        if (targetSymbol is null)
        {
            return;
        }

        // Target walks its receiver chain too — `parent.Id = value` carries Parent's
        // receiver context just like a read would, so assignments use the same multi-tag
        // view on both sides.
        var targetInfo = GetAccessInfo(assignment.Target, config);
        var sourceSymbol = assignment.Value.GetReferencedSymbol();
        var sourceInfo = GetAccessInfo(assignment.Value, config);
        Report(
            context,
            assignment.Value.Syntax.GetLocation(),
            sourceSymbol,
            sourceInfo,
            targetSymbol,
            targetInfo,
            config);
    }

    static void AnalyzePropertyInitializer(OperationAnalysisContext context, Config config)
    {
        var init = (IPropertyInitializerOperation)context.Operation;
        var sourceSymbol = init.Value.GetReferencedSymbol();
        var sourceInfo = GetAccessInfo(init.Value, config);
        foreach (var property in init.InitializedProperties)
        {
            var targetInfo = GetIdWithInheritance(property, config);
            Report(
                context,
                init.Value.Syntax.GetLocation(),
                sourceSymbol,
                sourceInfo,
                property,
                targetInfo,
                config);
        }
    }

    static void AnalyzeFieldInitializer(OperationAnalysisContext context, Config config)
    {
        var init = (IFieldInitializerOperation)context.Operation;
        var sourceSymbol = init.Value.GetReferencedSymbol();
        var sourceInfo = GetAccessInfo(init.Value, config);
        foreach (var field in init.InitializedFields)
        {
            var targetInfo = GetIdWithInheritance(field, config);
            Report(
                context,
                init.Value.Syntax.GetLocation(),
                sourceSymbol,
                sourceInfo,
                field,
                targetInfo,
                config);
        }
    }


    // Expression-level resolution. Differs from symbol-only resolution in that a member
    // named `Id` walks the receiver's static-type chain (up to the declaring type) and
    // adds the convention tag for each level — so `child1.Id` carries both "Child1" and
    // "Base" and can flow into parameters tagged either way.
    //
    // An `[Id]` on a collection-typed member (e.g. `[Id("Customer")] IEnumerable<Guid>`)
    // describes its elements, not the collection-as-a-scalar. For scalar-compare
    // contexts (argument/assignment/equality of a whole collection), the scalar tag is
    // suppressed — otherwise passing the collection into a LINQ-shape extension's
    // receiver slot would fire SIA003 against the untagged passthrough parameter.
    // Element-tag consumers (foreach, .First(), lambda binding, chain walks) bypass
    // this path via GetReceiverElementTags so they still see the tag.
    static IdInfo GetAccessInfo(IOperation operation, Config config)
    {
        operation = operation.Unwrap();
        switch (operation)
        {
            case IPropertyReferenceOperation prop:
                return SuppressCollectionTag(
                    prop.Property.Type,
                    GetMemberAccessInfo(prop.Property, prop.Instance?.Type, config));
            case IFieldReferenceOperation field:
                return SuppressCollectionTag(
                    field.Field.Type,
                    GetMemberAccessInfo(field.Field, field.Instance?.Type, config));
            case IParameterReferenceOperation param:
                if (TryResolveLambdaParameterFromLinq(param, config, out var lambdaInfo))
                {
                    return lambdaInfo;
                }

                return SuppressCollectionTag(
                    param.Parameter.Type,
                    GetIdWithInheritance(param.Parameter, config));
            case ILocalReferenceOperation local:
                if (config.LocalBindings.TryGetValue(local.Local, out var boundTags))
                {
                    return IdInfo.Present(boundTags);
                }

                return IdInfo.Unknown;
            case IInvocationOperation invocation:
                if (TryResolveLinqElementReturn(invocation, config, out var linqInfo))
                {
                    return linqInfo;
                }

                return SuppressCollectionTag(
                    invocation.Type,
                    GetReturnInfo(invocation.TargetMethod, config));
            case IArrayElementReferenceOperation arrayElement:
                return GetReceiverElementTags(arrayElement.ArrayReference, config);
            default:
                return IdInfo.Unknown;
        }
    }

    // If the expression's static type is a single-T collection, any tag we read is
    // semantically an element tag — not applicable in scalar contexts.
    static IdInfo SuppressCollectionTag(ITypeSymbol? type, IdInfo info)
    {
        if (info.State != IdState.Present)
        {
            return info;
        }

        if (type.TryGetEnumerableElementType() is not null)
        {
            return IdInfo.Unknown;
        }

        return info;
    }

    // `foreach (var x in collection)` binds the loop variable `x` to the collection's
    // element tag. Locals don't support attributes in C#, so this lookup is the only
    // way tag flows through a foreach. Nested foreach over a tagged source works too
    // because the receiver resolution recurses through element-preserving calls.
    static void AnalyzeLoop(OperationAnalysisContext context, Config config)
    {
        if (context.Operation is not IForEachLoopOperation forEach)
        {
            return;
        }

        var tags = GetReceiverElementTags(forEach.Collection, config);
        if (tags.State != IdState.Present)
        {
            return;
        }

        var loopVar = ExtractLoopLocal(forEach.LoopControlVariable);
        if (loopVar is null)
        {
            return;
        }

        config.LocalBindings.TryAdd(loopVar, tags.Tags);
    }

    static ILocalSymbol? ExtractLoopLocal(IOperation? controlVariable) =>
        controlVariable switch
        {
            IVariableDeclaratorOperation decl => decl.Symbol,
            ILocalReferenceOperation localRef => localRef.Local,
            _ => null
        };

    // If `param` is a single-parameter LINQ-style lambda (e.g. `Where`, `Select`, `Any`
    // body, or any extension method on IEnumerable<T> accepting a Func<T,...>), and the
    // enclosing invocation's receiver is a collection carrying an element tag, bind the
    // lambda parameter to that element tag. This is what lets
    // `ids.Any(id => id == customerId)` resolve `id` without requiring an attribute on
    // the lambda parameter — attributes aren't even legal inside expression trees
    // (CS8972), so inference is the only way `IQueryable` predicates work.
    //
    // The gate is shape-based rather than name-based: any extension whose first
    // parameter is an IEnumerable<T> / array participates, so third-party helpers like
    // MoreLINQ.ForEach or custom paging extensions flow tags the same way built-in LINQ
    // does. Element-returning calls (First/Single/etc.) are kept on a closed allowlist
    // because their semantic is specific; see TryResolveLinqElementReturn.
    static bool TryResolveLambdaParameterFromLinq(
        IParameterReferenceOperation param,
        Config config,
        out IdInfo info)
    {
        info = IdInfo.Unknown;

        if (param.Parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.LambdaMethod } lambdaMethod)
        {
            return false;
        }

        // Only bind the lambda's first parameter — TSource in every IEnumerable<T>
        // extension shape. Index overloads (Select/Where with int) take the TSource as
        // parameter 0 too, so this covers them. Multi-source shapes like Zip /
        // SelectMany with an intermediate collection are not handled in this pass.
        if (param.Parameter.Ordinal != 0 ||
            lambdaMethod.Parameters.Length is 0 or > 2)
        {
            return false;
        }

        var anonymous = FindEnclosingAnonymousFunction(param);
        if (anonymous is null)
        {
            return false;
        }

        var invocation = FindEnclosingLinqInvocation(anonymous);
        if (invocation is null)
        {
            return false;
        }

        if (!IsEnumerableShapeExtension(invocation.TargetMethod))
        {
            return false;
        }

        var receiver = GetLinqReceiver(invocation);
        if (receiver is null)
        {
            return false;
        }

        var element = receiver.Type.TryGetEnumerableElementType();
        if (element is null ||
            !SymbolEqualityComparer.Default.Equals(element, param.Parameter.Type))
        {
            return false;
        }

        var elementTags = GetReceiverElementTags(receiver, config);
        if (elementTags.State != IdState.Present)
        {
            return false;
        }

        info = elementTags;
        return true;
    }

    // For element-returning LINQ (`.First()`, `.Single()`, `.ElementAt()` etc.) surface
    // the receiver's element tag as the invocation's result tag — so `customerIds.First()`
    // is treated as a single Customer id.
    static bool TryResolveLinqElementReturn(
        IInvocationOperation invocation,
        Config config,
        out IdInfo info)
    {
        info = IdInfo.Unknown;

        if (!invocation.TargetMethod.IsLinqMethod() ||
            !IsElementReturningLinq(invocation.TargetMethod.Name))
        {
            return false;
        }

        var receiver = GetLinqReceiver(invocation);
        if (receiver is null)
        {
            return false;
        }

        var element = receiver.Type.TryGetEnumerableElementType();
        if (element is null ||
            !SymbolEqualityComparer.Default.Equals(element, invocation.Type))
        {
            return false;
        }

        var tags = GetReceiverElementTags(receiver, config);
        if (tags.State != IdState.Present)
        {
            return false;
        }

        info = tags;
        return true;
    }

    // Walk element-preserving calls backwards until we hit an expression whose symbol
    // (property/field/parameter/return) carries explicit [Id] tags on a collection-typed
    // declaration. Convention tagging is not applied here — the receiver has to be
    // explicitly tagged for element flow, otherwise generic collections without [Id]
    // would spuriously propagate.
    //
    // Select/SelectMany get their own handling (GetSelectElementTags) because the result
    // element type can differ from the source — the selector decides the new tag.
    static IdInfo GetReceiverElementTags(IOperation receiver, Config config)
    {
        // Iterates element-preserving LINQ chains instead of recursing — long method
        // chains (common in query-heavy code) would otherwise grow the stack linearly
        // with chain length.
        while (true)
        {
            receiver = receiver.Unwrap();

            if (receiver is IInvocationOperation inv)
            {
                var targetMethod = inv.TargetMethod;

                if (IsSelectCall(targetMethod))
                {
                    return GetSelectElementTags(inv, config);
                }

                if (IsElementPreserving(targetMethod))
                {
                    var next = GetLinqReceiver(inv);
                    if (next is null)
                    {
                        return IdInfo.Unknown;
                    }

                    receiver = next;
                    continue;
                }
            }

            var symbol = receiver.GetReferencedSymbol();
            if (symbol is null)
            {
                return IdInfo.Unknown;
            }

            var symbolType = symbol.GetDeclaredType();
            if (symbolType.TryGetEnumerableElementType() is null)
            {
                return IdInfo.Unknown;
            }

            return GetExplicitCollectionTags(symbol, config);
        }
    }

    // Resolve `[Id]` / `[UnionId]` attributes for a collection-typed symbol, walking
    // the override and interface-implementation chain so an interface-declared tag
    // flows through its implementations. Convention tagging (name-based) is
    // deliberately skipped — a collection-typed member whose name happens to match
    // the `Id` / `XxxId` convention would spuriously acquire a tag that no caller
    // can opt out of.
    static IdInfo GetExplicitCollectionTags(ISymbol symbol, Config config)
    {
        if (TryGetFromIndex(symbol, config, out var indexed))
        {
            return indexed.IsDefaultOrEmpty ? IdInfo.NotPresent : IdInfo.Present(indexed);
        }

        if (symbol is IMethodSymbol method)
        {
            return GetReturnInfo(method, config);
        }

        var direct = GetIdFromAttributes(symbol.GetAttributes());
        if (direct.State == IdState.Present)
        {
            return direct;
        }

        if (symbol is IPropertySymbol property)
        {
            var inherited = GetPropertyIdFromHierarchy(property);
            if (inherited.State == IdState.Present)
            {
                return inherited;
            }

            if (FindRecordPrimaryParameter(property) is { } recordParameter)
            {
                var fromParameter = GetIdFromAttributes(recordParameter.GetAttributes());
                if (fromParameter.State == IdState.Present)
                {
                    return fromParameter;
                }
            }
        }
        else if (symbol is IParameterSymbol parameter)
        {
            var inherited = GetParameterIdFromHierarchy(parameter);
            if (inherited.State == IdState.Present)
            {
                return inherited;
            }
        }

        var implicitTags = GetImplicitTagsFromContainingGenerics(symbol);
        if (!implicitTags.IsDefaultOrEmpty)
        {
            return IdInfo.PresentExplicit(implicitTags);
        }

        return IdInfo.NotPresent;
    }

    // Select/SelectMany can preserve, transform, or drop the tag depending on the
    // selector. Three shapes are recognised, all statically inspectable:
    //   1. Identity lambda `x => x` — result element tag = receiver element tag.
    //   2. Method group `.Select(SomeMethod)` — result tag = method's [return: Id].
    //   3. Expression-bodied lambda whose body resolves to a known tag.
    // Other selector shapes (multi-statement lambdas, untagged expressions) drop the tag.
    static IdInfo GetSelectElementTags(IInvocationOperation invocation, Config config)
    {
        var selector = FindSelectorArgument(invocation);
        if (selector is null)
        {
            return IdInfo.Unknown;
        }

        selector = selector.Unwrap();

        if (selector is IDelegateCreationOperation creation)
        {
            var target = creation.Target.Unwrap();

            if (target is IMethodReferenceOperation methodRef)
            {
                return GetReturnInfo(methodRef.Method, config);
            }

            if (target is IAnonymousFunctionOperation lambda)
            {
                var body = GetSingleReturnExpression(lambda);
                if (body is null)
                {
                    return IdInfo.Unknown;
                }

                if (IsIdentityReference(body, lambda))
                {
                    var next = GetLinqReceiver(invocation);
                    if (next is null)
                    {
                        return IdInfo.Unknown;
                    }

                    return GetReceiverElementTags(next, config);
                }

                return GetAccessInfo(body, config);
            }
        }

        return IdInfo.Unknown;
    }

    // The selector sits after the source in Enumerable/Queryable.Select; for extension
    // calls the source is Arguments[0] and the selector Arguments[1]. For instance-form
    // Select (custom providers), Instance is the source and Arguments[0] is the selector.
    static IOperation? FindSelectorArgument(IInvocationOperation invocation)
    {
        if (invocation.Instance is not null)
        {
            return invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null;
        }

        if (invocation.TargetMethod.IsExtensionMethod &&
            invocation.Arguments.Length > 1)
        {
            return invocation.Arguments[1].Value;
        }

        return null;
    }

    // Lambda bodies surface as a synthesised block with a single return — both for
    // expression-bodied and brace-bodied single-return lambdas. Anything with more than
    // one statement we treat as opaque (the last statement isn't reliably the result).
    static IOperation? GetSingleReturnExpression(IAnonymousFunctionOperation lambda)
    {
        var block = lambda.Body;
        if (block.Operations.Length != 1)
        {
            return null;
        }

        if (block.Operations[0] is IReturnOperation { ReturnedValue: { } value })
        {
            return value.Unwrap();
        }

        return null;
    }

    // Identity projection: the body references the lambda's single input parameter
    // unchanged. This is what makes `.Select(x => x)` a no-op for tag tracking.
    static bool IsIdentityReference(IOperation body, IAnonymousFunctionOperation lambda)
    {
        if (body is not IParameterReferenceOperation paramRef)
        {
            return false;
        }

        var parameters = lambda.Symbol.Parameters;
        return parameters.Length > 0 &&
               SymbolEqualityComparer.Default.Equals(paramRef.Parameter, parameters[0]);
    }

    // Element preservation is accepted via two channels: the named-LINQ list (closed,
    // covers every System.Linq.Enumerable/Queryable method whose signature matches
    // IEnumerable<T> → IEnumerable<T>), and a shape-based rule that lets third-party
    // extensions with the same signature participate — MoreLINQ, EF `.Include`,
    // custom paging helpers, etc. The shape rule requires the method to be an extension
    // on IEnumerable<T> whose return is also IEnumerable<T> with the same element T.
    //
    // Comparison runs on OriginalDefinition so that generic methods declared as
    // `T Foo<T>(IEnumerable<T>) → IEnumerable<T>` match — without OriginalDefinition
    // the input type parameter and return type parameter are distinct symbols after
    // construction, which would defeat the check.
    static bool IsElementPreserving(IMethodSymbol method)
    {
        if (method.IsLinqMethod() && IsElementPreservingLinq(method.Name))
        {
            return true;
        }

        if (!method.IsExtensionMethod)
        {
            return false;
        }

        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (definition.Parameters.Length == 0)
        {
            return false;
        }

        var inputElement = definition.Parameters[0].Type.TryGetEnumerableElementType();
        var outputElement = definition.ReturnType.TryGetEnumerableElementType();
        return inputElement is not null &&
               outputElement is not null &&
               SymbolEqualityComparer.Default.Equals(inputElement, outputElement);
    }

    static bool IsSelectCall(IMethodSymbol method) =>
        method.IsLinqMethod() &&
        method.Name is "Select" or "SelectMany";

    // An extension method whose receiver carries a discoverable element type.
    // This is the gate for LINQ-shaped recognition — it lets `static T[] Custom<T>(this
    // IEnumerable<T> src, Func<T,bool> f)` flow tags without hardcoding the method name.
    static bool IsEnumerableShapeExtension(IMethodSymbol method) =>
        GetExtensionReceiverType(method) is { } receiverType &&
        receiverType.TryGetEnumerableElementType() is not null;

    // For a reduced extension-method call (`x.Ext(...)`), `method.Parameters` excludes
    // the receiver — the "this" parameter only appears on the unreduced symbol, which
    // ReducedFrom surfaces. For calls written in static form (`Ext(x, ...)`) the method
    // is already unreduced, so ReducedFrom is null and Parameters[0] is the receiver.
    static ITypeSymbol? GetExtensionReceiverType(IMethodSymbol method)
    {
        if (!method.IsExtensionMethod)
        {
            return null;
        }

        var full = method.ReducedFrom ?? method;
        if (full.Parameters.Length == 0)
        {
            return null;
        }

        return full.Parameters[0].Type;
    }


    static IOperation? FindEnclosingAnonymousFunction(IOperation operation)
    {
        var current = operation.Parent;
        while (current is not null)
        {
            if (current is IAnonymousFunctionOperation)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    static IInvocationOperation? FindEnclosingLinqInvocation(IOperation lambda)
    {
        var current = lambda.Parent;
        while (current is not null)
        {
            if (current is IInvocationOperation invocation)
            {
                return invocation;
            }

            // Walk through delegate creation, conversion, argument wrappers that the
            // compiler threads between the lambda and the invocation. An unrelated
            // enclosing operation (e.g. a different invocation body) means the lambda
            // isn't a direct argument of the LINQ call we care about.
            if (current is IDelegateCreationOperation or IConversionOperation or IArgumentOperation)
            {
                current = current.Parent;
                continue;
            }

            return null;
        }

        return null;
    }

    // Extension-method invocations of LINQ put the receiver in Arguments[0] and leave
    // Instance null. Instance-method LINQ (rare but e.g. Queryable instance forms on
    // custom providers) uses Instance. Handle both so both shapes propagate.
    static IOperation? GetLinqReceiver(IInvocationOperation invocation)
    {
        if (invocation.Instance is not null)
        {
            return invocation.Instance;
        }

        if (invocation.TargetMethod.IsExtensionMethod &&
            invocation.Arguments.Length > 0)
        {
            return invocation.Arguments[0].Value;
        }

        return null;
    }

    static bool IsElementReturningLinq(string methodName) =>
        methodName is
            "First" or "FirstOrDefault" or
            "Single" or "SingleOrDefault" or
            "Last" or "LastOrDefault" or
            "ElementAt" or "ElementAtOrDefault" or
            "Min" or "Max" or
            "Aggregate";

    static bool IsElementPreservingLinq(string methodName) =>
        methodName is
            "Where" or
            "OrderBy" or "OrderByDescending" or
            "ThenBy" or "ThenByDescending" or
            "Reverse" or
            "Take" or "TakeWhile" or "TakeLast" or
            "Skip" or "SkipWhile" or "SkipLast" or
            "Distinct" or "DistinctBy" or
            "Concat" or "Union" or "UnionBy" or
            "Intersect" or "IntersectBy" or
            "Except" or "ExceptBy" or
            "AsEnumerable" or "AsQueryable" or
            "ToArray" or "ToList" or "ToHashSet" or
            "Append" or "Prepend";

    // Resolve tags on a method's return value. `[return: Id("Order")]` / `[return: UnionId(...)]`
    // on the method itself, or on any method it overrides / interface member it implements.
    // No naming convention — method names aren't a reliable tag source. Absence of an
    // attribute produces Unknown (not NotPresent) so untagged invocations like
    // `Guid.NewGuid()` stay silent — same policy as local variables and literals.
    static IdInfo GetReturnInfo(IMethodSymbol method, Config config)
    {
        if (TryGetFromIndex(method, config, out var indexed))
        {
            return indexed.IsDefaultOrEmpty ? IdInfo.Unknown : IdInfo.Present(indexed);
        }

        var direct = GetIdFromAttributes(method.GetReturnTypeAttributes());
        if (direct.State == IdState.Present)
        {
            return direct;
        }

        var overridden = method.OverriddenMethod;
        while (overridden is not null)
        {
            var info = GetIdFromAttributes(overridden.GetReturnTypeAttributes());
            if (info.State == IdState.Present)
            {
                return info;
            }

            overridden = overridden.OverriddenMethod;
        }

        foreach (var ifaceMember in method.ExplicitInterfaceImplementations)
        {
            var info = GetIdFromAttributes(ifaceMember.GetReturnTypeAttributes());
            if (info.State == IdState.Present)
            {
                return info;
            }
        }

        var containingType = method.ContainingType;
        if (containingType is not null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var ifaceMember in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                    if (!SymbolEqualityComparer.Default.Equals(impl, method))
                    {
                        continue;
                    }

                    var info = GetIdFromAttributes(ifaceMember.GetReturnTypeAttributes());
                    if (info.State == IdState.Present)
                    {
                        return info;
                    }
                }
            }
        }

        return IdInfo.Unknown;
    }

    static IdInfo GetMemberAccessInfo(ISymbol member, ITypeSymbol? receiverType, Config config)
    {
        // Pre-resolved index covers the library-side (member + its declaring-type receiver)
        // tag set directly. When hit, skip EnumerateMemberChain (AllInterfaces walk) AND
        // the receiver-type walk — the producer has already folded those contributions in
        // for the concrete types it owns. Consumer-side subclass receivers fall through
        // because ContainingAssembly is the source assembly.
        if (TryGetFromIndex(member, config, out var indexed))
        {
            return indexed.IsDefaultOrEmpty ? IdInfo.NotPresent : IdInfo.Present(indexed);
        }

        var receiverTags = ImmutableArray.CreateBuilder<string>();
        var memberTags = ImmutableArray.CreateBuilder<string>();
        var explicitTags = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var explicitSeen = new HashSet<string>(StringComparer.Ordinal);
        var coveredTypes = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // 1. Walk the property's override + interface chain. At every level contribute
        //    the level's explicit [Id] tag, or — if the level has none — the convention
        //    tag for that level (type name for `Id`, prefix for `XxxId`). Explicit and
        //    convention tags merge into one set; users who want to broaden a convention
        //    tag stack explicit [Id]s on base / override members.
        foreach (var level in EnumerateMemberChain(member))
        {
            if (level.ContainingType is { } ct)
            {
                coveredTypes.Add(ct.OriginalDefinition);
            }

            var explicitInfo = GetIdFromAttributes(level.GetAttributes());
            if (explicitInfo.State == IdState.Present)
            {
                foreach (var tag in explicitInfo.Tags)
                {
                    if (seen.Add(tag))
                    {
                        memberTags.Add(tag);
                    }

                    if (explicitSeen.Add(tag))
                    {
                        explicitTags.Add(tag);
                    }
                }

                continue;
            }

            // Record primary-ctor parameters carry their [Id] / [UnionId] on the
            // parameter itself, not the synthesized property. Treat those tags as if
            // they lived on the property so receiver-chain walking sees them.
            if (level is IPropertySymbol recordProperty &&
                FindRecordPrimaryParameter(recordProperty) is { } recordParameter)
            {
                var parameterInfo = GetIdFromAttributes(recordParameter.GetAttributes());
                if (parameterInfo.State == IdState.Present)
                {
                    foreach (var tag in parameterInfo.Tags)
                    {
                        if (seen.Add(tag))
                        {
                            memberTags.Add(tag);
                        }

                        if (explicitSeen.Add(tag))
                        {
                            explicitTags.Add(tag);
                        }
                    }

                    continue;
                }
            }

            // Convention only applies to user-declared members. Library-declared levels
            // in the chain (e.g. an interface member in a referenced assembly) are
            // silently skipped — the user can't attach a tag to them.
            if (level.DeclaringSyntaxReferences.IsEmpty)
            {
                continue;
            }

            if (level is IPropertySymbol { IsIndexer: true })
            {
                continue;
            }

            if (TryGetConventionName(level, out var convName))
            {
                if (seen.Add(convName))
                {
                    memberTags.Add(convName);
                }
            }
        }

        // 2. Receiver-type walk adds `Id`-convention tags for types in the static-receiver
        //    chain that were NOT covered by the member-chain walk. This catches the case
        //    where a derived type inherits `Id` without redeclaring it — the receiver-type
        //    contributes its conventional tag so `child1.Id` still carries "Child1".
        //    Only the `Id` rule applies here; `XxxId` is name-based and already captured.
        //    Stop at System.Object so "Object" never leaks into the tag set — relevant
        //    when the declaring type is an interface and `.BaseType` walks past it.
        if (member is { Name: "Id", ContainingType: { } memberContaining } &&
            receiverType is INamedTypeSymbol rt)
        {
            var boundary = memberContaining.OriginalDefinition;
            var current = rt;
            // Defensive cap against runaway walks from ill-formed metadata.
            for (var safety = 32; current is not null && safety > 0; safety--)
            {
                if (current.SpecialType == SpecialType.System_Object)
                {
                    break;
                }

                if (!coveredTypes.Contains(current.OriginalDefinition) &&
                    current.Name.Length > 0 &&
                    seen.Add(current.Name))
                {
                    receiverTags.Add(current.Name);
                }

                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, boundary))
                {
                    break;
                }

                current = current.BaseType;
            }
        }

        // Emit receiver-type tags (most-derived → base) before member-chain tags so the
        // single-value accessor used by code fixes picks the receiver's static type. For
        // `treasuryBid.Id` inherited from `BaseEntity`, FirstValue is "TreasuryBid"
        // rather than "BaseEntity" — matches what the user reads locally at the call
        // site and produces the more useful "Rename to treasuryBidId" suggestion.
        if (receiverTags.Count == 0 && memberTags.Count == 0)
        {
            return IdInfo.NotPresent;
        }

        return IdInfo.Present([.. receiverTags, .. memberTags], explicitTags.ToImmutable());
    }

    // Yields the member and then every override/interface-impl target reachable from it.
    // `new`-hide is NOT followed (OverriddenProperty returns null for it) — the hidden
    // declaration is a fresh member that the user has explicitly disconnected from the
    // base. Parameters are single-tag today and don't pass through this enumerator.
    static IEnumerable<ISymbol> EnumerateMemberChain(ISymbol member)
    {
        yield return member;

        if (member is not IPropertySymbol property)
        {
            yield break;
        }

        var overridden = property.OverriddenProperty;
        while (overridden is not null)
        {
            yield return overridden;
            overridden = overridden.OverriddenProperty;
        }

        foreach (var ifaceMember in property.ExplicitInterfaceImplementations)
        {
            yield return ifaceMember;
        }

        var containingType = property.ContainingType;
        if (containingType is null)
        {
            yield break;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(property.Name).OfType<IPropertySymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    yield return ifaceMember;
                }
            }
        }
    }

    // Reads the [Id] attribute off a symbol, walking override / interface-impl chains
    // for properties so a derived class inherits the base's tag without having to
    // repeat the attribute. `new`-hide is NOT walked — `new` declares a fresh property
    // and the user is explicitly saying "this is not the base's property".
    //
    // Resolution order:
    //   1. Explicit [Id] on the symbol itself.
    //   2. Inherited [Id] via override/interface impl chain (properties & parameters).
    //   3. Naming convention (properties/fields only).
    //   4. NotPresent.
    static IdInfo GetIdWithInheritance(ISymbol symbol, Config config)
    {
        if (TryGetFromIndex(symbol, config, out var indexed))
        {
            return indexed.IsDefaultOrEmpty ? IdInfo.NotPresent : IdInfo.Present(indexed);
        }

        var direct = GetIdFromAttributes(symbol.GetAttributes());
        if (direct.State == IdState.Present)
        {
            return direct;
        }

        if (symbol is IPropertySymbol property)
        {
            var inherited = GetPropertyIdFromHierarchy(property);
            if (inherited.State == IdState.Present)
            {
                return inherited;
            }

            if (FindRecordPrimaryParameter(property) is { } recordParameter)
            {
                var fromParameter = GetIdFromAttributes(recordParameter.GetAttributes());
                if (fromParameter.State == IdState.Present)
                {
                    return fromParameter;
                }
            }
        }
        else if (symbol is IParameterSymbol parameter)
        {
            var inherited = GetParameterIdFromHierarchy(parameter);
            if (inherited.State == IdState.Present)
            {
                return inherited;
            }
        }

        // Convention applies only to user-declared symbols. Library members (e.g.
        // `Diagnostic.Id`, `DateTime.Now`) never receive automatic tags — otherwise any
        // property named `Id` in referenced assemblies would suddenly carry a tag the
        // user can't change.
        if (!symbol.DeclaringSyntaxReferences.IsEmpty &&
            TryGetConventionName(symbol, out var conventionName))
        {
            return IdInfo.Present(conventionName);
        }

        return direct;
    }

    static IdInfo GetParameterIdFromHierarchy(IParameterSymbol parameter)
    {
        if (parameter.ContainingSymbol is not IMethodSymbol method)
        {
            return IdInfo.NotPresent;
        }

        var ordinal = parameter.Ordinal;

        var overridden = method.OverriddenMethod;
        while (overridden is not null)
        {
            if (ordinal < overridden.Parameters.Length)
            {
                var info = GetIdFromAttributes(
                    overridden.Parameters[ordinal].GetAttributes());
                if (info.State == IdState.Present)
                {
                    return info;
                }
            }

            overridden = overridden.OverriddenMethod;
        }

        foreach (var ifaceMember in method.ExplicitInterfaceImplementations)
        {
            if (ordinal >= ifaceMember.Parameters.Length)
            {
                continue;
            }

            var info = GetIdFromAttributes(
                ifaceMember.Parameters[ordinal].GetAttributes());
            if (info.State == IdState.Present)
            {
                return info;
            }
        }

        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return IdInfo.NotPresent;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (!SymbolEqualityComparer.Default.Equals(impl, method))
                {
                    continue;
                }

                if (ordinal >= ifaceMember.Parameters.Length)
                {
                    continue;
                }

                var info = GetIdFromAttributes(
                    ifaceMember.Parameters[ordinal].GetAttributes());
                if (info.State == IdState.Present)
                {
                    return info;
                }
            }
        }

        return IdInfo.NotPresent;
    }

    static IdInfo GetPropertyIdFromHierarchy(IPropertySymbol property)
    {
        // Walk the `override` chain bottom-up. First [Id] found wins.
        var overridden = property.OverriddenProperty;
        while (overridden is not null)
        {
            var info = GetIdFromAttributes(overridden.GetAttributes());
            if (info.State == IdState.Present)
            {
                return info;
            }

            overridden = overridden.OverriddenProperty;
        }

        // Explicit interface implementations carry their target interface member directly.
        foreach (var ifaceMember in property.ExplicitInterfaceImplementations)
        {
            var info = GetIdFromAttributes(ifaceMember.GetAttributes());
            if (info.State == IdState.Present)
            {
                return info;
            }
        }

        // Implicit interface impls: walk the containing type's interfaces and ask each
        // whether this property satisfies one of its members.
        var containingType = property.ContainingType;
        if (containingType is null)
        {
            return IdInfo.NotPresent;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(property.Name).OfType<IPropertySymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (!SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    continue;
                }

                var info = GetIdFromAttributes(ifaceMember.GetAttributes());
                if (info.State == IdState.Present)
                {
                    return info;
                }
            }
        }

        return IdInfo.NotPresent;
    }

    // Peel off conversions and `await` so the resolver sees the value-producing operation
    // underneath. An `await task` result carries the tag of the method that produced the
    // task, so unwrapping lets `[return: Id]` on an async method flow through the await.
    static IdInfo GetIdFromAttributes(ImmutableArray<AttributeData> attributes)
    {
        // Id and UnionId on the same declaration are rare but legal (different attribute
        // classes). If they coexist the tags union, which is consistent with the rest of
        // the analyzer treating Id-family attrs as a single set.
        var tags = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            if (IsAttributeNamed(attribute, idMetadataName))
            {
                if (attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is string { Length: > 0 } s &&
                    seen.Add(s))
                {
                    tags.Add(s);
                }

                continue;
            }

            if (TryGetGenericIdTag(attribute, out var genericTag))
            {
                if (seen.Add(genericTag))
                {
                    tags.Add(genericTag);
                }

                continue;
            }

            if (IsAttributeNamed(attribute, unionIdMetadataName))
            {
                foreach (var option in ExtractUnionOptions(attribute))
                {
                    if (seen.Add(option))
                    {
                        tags.Add(option);
                    }
                }

                continue;
            }

            if (TryGetGenericUnionIdTags(attribute, out var genericUnionTags))
            {
                foreach (var option in genericUnionTags)
                {
                    if (seen.Add(option))
                    {
                        tags.Add(option);
                    }
                }
            }
        }

        return tags.Count == 0
            ? IdInfo.NotPresent
            : IdInfo.PresentExplicit(tags.ToImmutable());
    }

    // Reads `[UnionId(params string[] options)]`'s constructor argument. Roslyn surfaces
    // the `params string[]` as a single Array TypedConstant whose Values are the items.
    static ImmutableArray<string> ExtractUnionOptions(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
        {
            return [];
        }

        var first = attribute.ConstructorArguments[0];
        if (first.Kind != TypedConstantKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(first.Values.Length);
        foreach (var element in first.Values)
        {
            // Empty tags are dropped here — [Id("")] is never a valid shape, so
            // letting one through would propagate into diagnostics/codefixes that
            // round-trip the tag back into [Id("")] / [UnionId("", ...)] output.
            if (element.Value is string { Length: > 0 } s)
            {
                builder.Add(s);
            }
        }

        return builder.ToImmutable();
    }

    static void Report(
        OperationAnalysisContext context,
        Location location,
        ISymbol? sourceSymbol,
        IdInfo source,
        ISymbol targetSymbol,
        IdInfo target,
        Config config)
    {
        // Anonymous-type properties can't carry [Id], so any diagnostic against one as a
        // target has no fix site (covers SIA001/SIA002/SIA003 from a `new { X = expr }`
        // initializer's synthesized assignment, and any other write where the target lives
        // on an anonymous type). Reads from anon properties still carry their convention
        // tag — see TryGetConventionName.
        if (targetSymbol is IPropertySymbol { ContainingType.IsAnonymousType: true })
        {
            return;
        }

        if (source.State == IdState.Unknown ||
            target.State == IdState.Unknown)
        {
            return;
        }

        if (source.State == IdState.Present &&
            target.State == IdState.Present)
        {
            // Source and target sets must overlap — at least one shared tag means the
            // value could legitimately flow in. Intersection is symmetric, so it handles
            // both receiver-walked covariant sources (`child.Id` = {"Child","Base"}) and
            // union-tagged targets (`[UnionId("A","B")]` accepts "A" or "B") uniformly.
            // Widen the source only: a derived-tagged value can flow where a base-tagged
            // target is expected, but not the other way around.
            var widenedSource = Widen(source, config);
            if (!target.IntersectsWith(widenedSource))
            {
                // Attach both declarations as additional locations so the code fix can
                // offer to fix either side. Slot 0 is always the target; slot 1 is the
                // source (when user-owned; library refs are reported as null sentinel).
                context.ReportDiagnostic(Diagnostic.Create(
                    idMismatchRule,
                    location,
                    additionalLocations: GetMismatchLocations(targetSymbol, sourceSymbol),
                    properties: BuildMismatchProperties(source.FirstValue, target.FirstValue),
                    messageArgs: [source.Format(), target.Format()]));
            }

            return;
        }

        if (source.State == IdState.NotPresent &&
            target.State == IdState.Present)
        {
            // Don't fire when the untagged source is in referenced metadata (e.g. Guid.Empty).
            // The fix adds [Id] to the source's declaration; if we can't modify it, the
            // diagnostic is just noise.
            if (sourceSymbol is null ||
                sourceSymbol.DeclaringSyntaxReferences.IsEmpty)
            {
                return;
            }

            if (NamespaceSuppression.IsSuppressed(sourceSymbol, config.SuppressedNamespaces))
            {
                return;
            }

            // Param-name ↔ property-name correspondence: a parameter whose name maps
            // (first-char-upper) to the target property's name is the obvious carrier
            // for that property's value. The user has already expressed the binding
            // through naming — requiring an extra `[Id("X")]` on the parameter would be
            // noise. Covers primary-ctor `Tenant(string id) { Id { get; } = id; }`,
            // regular-ctor `Tenant(string id) => Id = id;`, and trivial setters
            // `void Reset(string id) => Id = id;` uniformly.
            //
            // Tag mismatches via an explicit attribute on the parameter aren't silenced
            // here — those flow through the SIA001 branch above (source is Present),
            // not this one (source is NotPresent).
            if (sourceSymbol is IParameterSymbol parameter &&
                targetSymbol is IPropertySymbol property &&
                ParameterNameCorrespondsToProperty(parameter.Name, property.Name))
            {
                return;
            }

            // Fix site is the source symbol's declaration (add Id matching target). The
            // codefix splits the pipe-delimited tags and offers one fix per option plus a
            // combined [UnionId(...)] when the target is multi-tag.
            context.ReportDiagnostic(CreateFixableDiagnostic(
                missingSourceIdRule,
                location,
                sourceSymbol,
                target));
            return;
        }

        if (source.State == IdState.Present &&
            target.State == IdState.NotPresent)
        {
            // Don't fire SIA003 when the target is declared in referenced metadata (BCL,
            // third-party libraries). Library authors can't apply [Id], and boundary APIs
            // like Equals(Guid), CompareTo, Dictionary<Guid,T>.this[Guid] would otherwise
            // produce constant noise when passing any tagged value to them.
            if (targetSymbol.DeclaringSyntaxReferences.IsEmpty)
            {
                return;
            }

            if (NamespaceSuppression.IsSuppressed(targetSymbol, config.SuppressedNamespaces))
            {
                return;
            }

            // Skip when the target's declared type can't meaningfully carry a tag —
            // `object` parameters/props (logging, serialization) and unconstrained
            // generics (`T`). Adding [Id] to these wouldn't express intent, and the
            // common case (passing a tagged id into an ILogger-like helper with an
            // `object` or `T` arg) would otherwise produce constant noise.
            if (IsBoundaryTarget(targetSymbol))
            {
                return;
            }

            // Fix site is the target symbol's declaration (add Id matching source).
            context.ReportDiagnostic(CreateFixableDiagnostic(
                droppedIdRule,
                location,
                targetSymbol,
                source));
        }
    }

    // Parameters are camelCase, properties PascalCase — so `id` ↔ `Id` and
    // `customerId` ↔ `CustomerId` should match. Compare ordinal after upper-casing
    // the parameter's first character, matching the same casing rule already used
    // by convention rule 2.
    static bool ParameterNameCorrespondsToProperty(string parameterName, string propertyName)
    {
        if (parameterName.Length == 0 || parameterName.Length != propertyName.Length)
        {
            return false;
        }

        if (char.ToUpperInvariant(parameterName[0]) != propertyName[0])
        {
            return false;
        }

        for (var i = 1; i < parameterName.Length; i++)
        {
            if (parameterName[i] != propertyName[i])
            {
                return false;
            }
        }

        return true;
    }

    static bool IsBoundaryTarget(ISymbol target)
    {
        // Anonymous-type members are filtered out earlier in Report, so they can't reach
        // here. The remaining checks cover targets whose declared type can't meaningfully
        // hold a tag.
        //
        // For parameters on a generic method, `.Type` is already substituted at the call
        // site (T → Guid). Inspect the original definition so the type-parameter check
        // actually catches `T`. Properties/fields don't have this issue since their
        // declared type is fixed.
        var type = target switch
        {
            IParameterSymbol parameter => parameter.OriginalDefinition.Type,
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        if (type is null)
        {
            return false;
        }

        return type.SpecialType == SpecialType.System_Object ||
               type.TypeKind == TypeKind.TypeParameter;
    }

    static Diagnostic CreateFixableDiagnostic(
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

        return [Location.Create(declaration.SyntaxTree, declaration.Span)];
    }

    // Build a positional [target, source] location array for SIA001. Slots without a
    // user-owned declaration (library refs, locals, etc.) use Location.None as a
    // sentinel so the slot index stays stable — the fixer checks IsInSource before
    // offering a fix for either side.
    static Location[] GetMismatchLocations(ISymbol? targetSymbol, ISymbol? sourceSymbol) =>
    [
        ResolveDeclarationLocation(targetSymbol),
        ResolveDeclarationLocation(sourceSymbol)
    ];

    static Location ResolveDeclarationLocation(ISymbol? symbol)
    {
        var declaration = symbol?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            return Location.None;
        }

        return Location.Create(declaration.SyntaxTree, declaration.Span);
    }

    static ImmutableDictionary<string, string?> BuildMismatchProperties(string? sourceValue, string? targetValue)
    {
        var properties = ImmutableDictionary<string, string?>.Empty
            // Kept for backward-compat: older fixer versions read IdValue and apply it
            // to AdditionalLocations[0] (the target). New fixer prefers the typed keys.
            .Add(ValueKey, sourceValue)
            .Add(TargetValueKey, sourceValue)
            .Add(SourceValueKey, targetValue);
        return properties;
    }

    enum IdState
    {
        Unknown,
        NotPresent,
        Present
    }

    // A value's Id info is a set of tags. Empty-with-state-Present is not allowed —
    // use NotPresent instead. Multi-tag sets arise from receiver-type walking at
    // access sites: `child1.Id` where `Id` is declared on Base carries both
    // "Child1" and "Base", so it satisfies parameters tagged either way.
    readonly struct IdInfo
    {
        public IdState State { get; }
        public ImmutableArray<string> Tags { get; }

        // Subset of Tags that came from explicit [Id]/[UnionId] attributes (as opposed
        // to convention inference from member name or receiver type). When non-empty,
        // SIA002/SIA003 fixes propose only these tags — guessing convention names onto
        // the untagged side would override the deliberate annotation on the tagged side.
        public ImmutableArray<string> ExplicitTags { get; }

        IdInfo(IdState state, ImmutableArray<string> tags, ImmutableArray<string> explicitTags)
        {
            State = state;
            Tags = tags;
            ExplicitTags = explicitTags;
        }

        public static IdInfo Unknown { get; } = new(IdState.Unknown, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        public static IdInfo NotPresent { get; } = new(IdState.NotPresent, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        public static IdInfo Present(string tag) =>
            new(IdState.Present, [tag], ImmutableArray<string>.Empty);

        public static IdInfo Present(ImmutableArray<string> tags) =>
            tags.IsDefaultOrEmpty
                ? NotPresent
                : new(IdState.Present, tags, ImmutableArray<string>.Empty);

        public static IdInfo Present(ImmutableArray<string> tags, ImmutableArray<string> explicitTags) =>
            tags.IsDefaultOrEmpty
                ? NotPresent
                : new(IdState.Present, tags, explicitTags.IsDefault ? ImmutableArray<string>.Empty : explicitTags);

        public static IdInfo PresentExplicit(ImmutableArray<string> tags) =>
            tags.IsDefaultOrEmpty
                ? NotPresent
                : new(IdState.Present, tags, tags);

        // Single-value accessor for the fixer (which needs one string to write back).
        // Picks the first tag — callers that care about multi-tag must use Tags directly.
        public string? FirstValue => Tags.IsDefaultOrEmpty ? null : Tags[0];

        // Set intersection — the source and target are compatible if they share at least
        // one tag. This is the natural rule for both covariant sources (receiver walk:
        // `child1.Id` carries {"Child1","Base"} so it matches a `[Id("Base")]` or
        // `[Id("Child1")]` parameter) and contravariant targets (`[UnionId("A","B")]`
        // accepts anything tagged "A" or "B").
        public bool IntersectsWith(IdInfo other)
        {
            foreach (var tag in Tags)
            {
                if (other.Tags.Contains(tag))
                {
                    return true;
                }
            }
            return false;
        }

        // Flat representation for diagnostic messages. Multi-tag sets use "/" as a
        // separator so a reader sees the full set at once: [Id("Child1/Base")].
        public string Format() =>
            Tags.IsDefaultOrEmpty ? "" : string.Join("/", Tags);
    }
}
