namespace StrongIdAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IdMismatchAnalyzer : DiagnosticAnalyzer
{
    public const string ValueKey = "IdValue";

    // .editorconfig key for overriding the default namespace suppression list.
    // Value is comma-separated; trailing `*` means prefix match (e.g. `System*` matches
    // `System`, `System.Collections`, etc.). Setting an empty value disables suppression.
    const string SuppressedNamespacesOption = "strongidanalyzer.suppressed_namespaces";

    // Library namespaces whose members we can't realistically tag. Noise for SIA002/SIA003
    // when a tagged id flows into BCL / framework APIs (e.g. logging, serialization,
    // dependency injection, Entity Framework). Users can override via .editorconfig.
    static readonly ImmutableArray<string> defaultSuppressedNamespaces =
        ["System*", "Microsoft*"];

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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [idMismatchRule, missingSourceIdRule, droppedIdRule, ambiguousConventionRule, redundantIdRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            // Resolve the source-generated IdAttribute once per compilation. If the consumer
            // has not had the source generator run (e.g. the package isn't referenced), this
            // returns null and the analyzer stays dormant.
            var idType = start.Compilation
                .GetTypeByMetadataName("StrongIdAnalyzer.IdAttribute");
            if (idType is null)
            {
                return;
            }

            var suppressedNamespaces = ReadSuppressedNamespaces(
                start.Options.AnalyzerConfigOptionsProvider);
            var config = new Config(idType, suppressedNamespaces);

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
                _ => CollectConvention(_, config, ambiguity, redundantCandidates),
                SymbolKind.Property,
                SymbolKind.Field,
                SymbolKind.Parameter);

            start.RegisterCompilationEndAction(
                _ => ReportConventionDiagnostics(_, ambiguity, redundantCandidates));
        });
    }

    static void CollectConvention(
        SymbolAnalysisContext context,
        Config config,
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

        var explicitAttribute = GetExplicitIdAttribute(symbol, config.IdType);

        // Only the containing-type-named rule (`public Guid Id`) feeds ambiguity tracking.
        // Explicit [Id] opts out — it resolves the ambiguity that SIA004 would otherwise
        // complain about.
        if (fromContainingType && explicitAttribute is null)
        {
            ambiguity
                .GetOrAdd(conventionName, _ => new())
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
                .Distinct<ISymbol>(SymbolEqualityComparer.Default)
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
                .Distinct<ISymbol>(SymbolEqualityComparer.Default)
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

    static AttributeData? GetExplicitIdAttribute(ISymbol symbol, INamedTypeSymbol idType)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, idType))
            {
                return attribute;
            }
        }

        return null;
    }

    static string? GetAttributeValue(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length > 0 &&
            attribute.ConstructorArguments[0].Value is string value)
        {
            return value;
        }

        return null;
    }

    static ImmutableArray<string> ReadSuppressedNamespaces(AnalyzerConfigOptionsProvider options)
    {
        if (!options.GlobalOptions.TryGetValue(SuppressedNamespacesOption, out var raw))
        {
            return defaultSuppressedNamespaces;
        }

        // Explicit empty disables all suppression.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(',')
            .Select(_ => _.Trim())
            .Where(_ => _.Length > 0)
            .ToImmutableArray();
    }

    static bool IsInSuppressedNamespace(ISymbol symbol, ImmutableArray<string> patterns)
    {
        if (patterns.IsEmpty)
        {
            return false;
        }

        var ns = symbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrEmpty(ns))
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern[pattern.Length - 1] == '*')
            {
                var prefix = pattern.Substring(0, pattern.Length - 1);
                if (prefix.Length == 0)
                {
                    return true;
                }

                // `System*` matches `System` exactly and any child namespace `System.*`
                // but not unrelated roots like `SystemX`.
                if (ns == prefix ||
                    ns!.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (ns == pattern)
            {
                return true;
            }
        }

        return false;
    }

    readonly struct Config(INamedTypeSymbol idType, ImmutableArray<string> suppressedNamespaces)
    {
        public INamedTypeSymbol IdType { get; } = idType;
        public ImmutableArray<string> SuppressedNamespaces { get; } = suppressedNamespaces;
    }

    static void AnalyzeBinaryOperator(OperationAnalysisContext context, Config config)
    {
        var idType = config.IdType;
        var operation = (IBinaryOperation)context.Operation;
        if (operation.OperatorKind != BinaryOperatorKind.Equals &&
            operation.OperatorKind != BinaryOperatorKind.NotEquals)
        {
            return;
        }

        var leftSymbol = GetSymbol(operation.LeftOperand);
        var rightSymbol = GetSymbol(operation.RightOperand);
        var leftInfo = GetAccessInfo(operation.LeftOperand, idType);
        var rightInfo = GetAccessInfo(operation.RightOperand, idType);

        // Equality is symmetric: if the two tag sets share any tag, the values could
        // represent the same identity. Only fire SIA001 when the sets are disjoint.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.Present)
        {
            if (leftInfo.IntersectsWith(rightInfo))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                idMismatchRule,
                operation.Syntax.GetLocation(),
                leftInfo.Format(),
                rightInfo.Format()));
            return;
        }

        // One side tagged, other is a user-owned member with no tag: SIA002 on the missing
        // side so the fixer can add [Id("<same value>")] to its declaration. When the
        // untagged side is Unknown (literal, Guid.Empty, local var, method call) no
        // diagnostic fires — those are routine and not a bug.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.NotPresent)
        {
            ReportMissingOnBinarySide(context, operation.RightOperand, rightSymbol, leftInfo.FirstValue, config);
            return;
        }

        if (leftInfo.State == IdState.NotPresent &&
            rightInfo.State == IdState.Present)
        {
            ReportMissingOnBinarySide(context, operation.LeftOperand, leftSymbol, rightInfo.FirstValue, config);
        }
    }

    static void ReportMissingOnBinarySide(
        OperationAnalysisContext context,
        IOperation untaggedOperand,
        ISymbol? untaggedSymbol,
        string? tag,
        Config config)
    {
        // Only fix user-owned declarations. Library members (Guid.Empty etc.) can't carry
        // [Id] so firing here would just be noise.
        if (untaggedSymbol is null ||
            untaggedSymbol.DeclaringSyntaxReferences.IsEmpty)
        {
            return;
        }

        if (IsInSuppressedNamespace(untaggedSymbol, config.SuppressedNamespaces))
        {
            return;
        }

        context.ReportDiagnostic(CreateFixableDiagnostic(
            missingSourceIdRule,
            untaggedOperand.Syntax.GetLocation(),
            untaggedSymbol,
            tag));
    }

    static void AnalyzeArgument(OperationAnalysisContext context, Config config)
    {
        var idType = config.IdType;
        var argument = (IArgumentOperation)context.Operation;
        var parameter = argument.Parameter;
        if (parameter is null)
        {
            return;
        }

        var targetInfo = GetIdWithInheritance(parameter, idType);
        var sourceSymbol = GetSymbol(argument.Value);
        var sourceInfo = GetAccessInfo(argument.Value, idType);
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
        var idType = config.IdType;
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        var targetSymbol = GetSymbol(assignment.Target);
        if (targetSymbol is null)
        {
            return;
        }

        // Target walks its receiver chain too — `parent.Id = value` carries Parent's
        // receiver context just like a read would, so assignments use the same multi-tag
        // view on both sides.
        var targetInfo = GetAccessInfo(assignment.Target, idType);
        var sourceSymbol = GetSymbol(assignment.Value);
        var sourceInfo = GetAccessInfo(assignment.Value, idType);
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
        var idType = config.IdType;
        var init = (IPropertyInitializerOperation)context.Operation;
        var sourceSymbol = GetSymbol(init.Value);
        var sourceInfo = GetAccessInfo(init.Value, idType);
        foreach (var property in init.InitializedProperties)
        {
            var targetInfo = GetIdWithInheritance(property, idType);
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
        var idType = config.IdType;
        var init = (IFieldInitializerOperation)context.Operation;
        var sourceSymbol = GetSymbol(init.Value);
        var sourceInfo = GetAccessInfo(init.Value, idType);
        foreach (var field in init.InitializedFields)
        {
            var targetInfo = GetIdWithInheritance(field, idType);
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

    static ISymbol? GetSymbol(IOperation operation)
    {
        operation = UnwrapConversions(operation);
        return operation switch
        {
            IPropertyReferenceOperation prop => prop.Property,
            IFieldReferenceOperation field => field.Field,
            IParameterReferenceOperation param => param.Parameter,
            _ => null
        };
    }

    // Expression-level resolution. Differs from symbol-only resolution in that a member
    // named `Id` walks the receiver's static-type chain (up to the declaring type) and
    // adds the convention tag for each level — so `child1.Id` carries both "Child1" and
    // "Base" and can flow into parameters tagged either way.
    static IdInfo GetAccessInfo(IOperation operation, INamedTypeSymbol idType)
    {
        operation = UnwrapConversions(operation);
        return operation switch
        {
            IPropertyReferenceOperation prop => GetMemberAccessInfo(prop.Property, prop.Instance?.Type, idType),
            IFieldReferenceOperation field => GetMemberAccessInfo(field.Field, field.Instance?.Type, idType),
            IParameterReferenceOperation param => GetIdWithInheritance(param.Parameter, idType),
            _ => IdInfo.Unknown
        };
    }

    static IdInfo GetMemberAccessInfo(ISymbol member, ITypeSymbol? receiverType, INamedTypeSymbol idType)
    {
        var tags = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

            var explicitInfo = GetIdFromAttributes(level.GetAttributes(), idType);
            if (explicitInfo.State == IdState.Present)
            {
                foreach (var tag in explicitInfo.Tags)
                {
                    if (seen.Add(tag))
                    {
                        tags.Add(tag);
                    }
                }

                continue;
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
                    tags.Add(convName);
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
        if (member.Name == "Id" &&
            member.ContainingType is { } memberContaining &&
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
                    tags.Add(current.Name);
                }

                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, boundary))
                {
                    break;
                }

                current = current.BaseType;
            }
        }

        return tags.Count == 0
            ? IdInfo.NotPresent
            : IdInfo.Present(tags.ToImmutable());
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

    static IdInfo GetId(ISymbol? symbol, INamedTypeSymbol idType)
    {
        if (symbol is null)
        {
            return IdInfo.Unknown;
        }

        return GetIdWithInheritance(symbol, idType);
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
    static IdInfo GetIdWithInheritance(ISymbol symbol, INamedTypeSymbol idType)
    {
        var direct = GetIdFromAttributes(symbol.GetAttributes(), idType);
        if (direct.State == IdState.Present)
        {
            return direct;
        }

        if (symbol is IPropertySymbol property)
        {
            var inherited = GetPropertyIdFromHierarchy(property, idType);
            if (inherited.State == IdState.Present)
            {
                return inherited;
            }
        }
        else if (symbol is IParameterSymbol parameter)
        {
            var inherited = GetParameterIdFromHierarchy(parameter, idType);
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

    static IdInfo GetParameterIdFromHierarchy(IParameterSymbol parameter, INamedTypeSymbol idType)
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
                    overridden.Parameters[ordinal].GetAttributes(),
                    idType);
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
                ifaceMember.Parameters[ordinal].GetAttributes(),
                idType);
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
                    ifaceMember.Parameters[ordinal].GetAttributes(),
                    idType);
                if (info.State == IdState.Present)
                {
                    return info;
                }
            }
        }

        return IdInfo.NotPresent;
    }

    static IdInfo GetPropertyIdFromHierarchy(IPropertySymbol property, INamedTypeSymbol idType)
    {
        // Walk the `override` chain bottom-up. First [Id] found wins.
        var overridden = property.OverriddenProperty;
        while (overridden is not null)
        {
            var info = GetIdFromAttributes(overridden.GetAttributes(), idType);
            if (info.State == IdState.Present)
            {
                return info;
            }

            overridden = overridden.OverriddenProperty;
        }

        // Explicit interface implementations carry their target interface member directly.
        foreach (var ifaceMember in property.ExplicitInterfaceImplementations)
        {
            var info = GetIdFromAttributes(ifaceMember.GetAttributes(), idType);
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

                var info = GetIdFromAttributes(ifaceMember.GetAttributes(), idType);
                if (info.State == IdState.Present)
                {
                    return info;
                }
            }
        }

        return IdInfo.NotPresent;
    }

    static IOperation UnwrapConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }
        return operation;
    }

    static IdInfo GetIdFromAttributes(
        ImmutableArray<AttributeData> attributes,
        INamedTypeSymbol idType)
    {
        foreach (var attribute in attributes)
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, idType))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string s)
            {
                return IdInfo.Present(s);
            }

            return IdInfo.Present("");
        }

        return IdInfo.NotPresent;
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
        if (source.State == IdState.Unknown ||
            target.State == IdState.Unknown)
        {
            return;
        }

        if (source.State == IdState.Present &&
            target.State == IdState.Present)
        {
            // The value flowing in must carry every tag the target demands. Multi-tag
            // targets (receiver-walked assignment LHS) force the source to cover all of
            // them; multi-tag sources (receiver-walked reads) satisfy any single-tag
            // target whose tag is in the set.
            if (!target.SatisfiedBy(source))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    idMismatchRule,
                    location,
                    source.Format(),
                    target.Format()));
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

            if (IsInSuppressedNamespace(sourceSymbol, config.SuppressedNamespaces))
            {
                return;
            }

            // Fix site is the source symbol's declaration (add Id matching target).
            // For multi-tag targets the fixer writes the first tag; the user can adjust.
            context.ReportDiagnostic(CreateFixableDiagnostic(
                missingSourceIdRule,
                location,
                sourceSymbol,
                target.FirstValue));
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

            if (IsInSuppressedNamespace(targetSymbol, config.SuppressedNamespaces))
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
                source.FirstValue));
        }
    }

    static bool IsBoundaryTarget(ISymbol target)
    {
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
        string? value) =>
        Diagnostic.Create(
            rule,
            location,
            additionalLocations: GetAdditionalLocations(fixTarget),
            properties: ImmutableDictionary<string, string?>.Empty.Add(ValueKey, value),
            messageArgs: value ?? "");

    static Location[]? GetAdditionalLocations(ISymbol? fixTarget)
    {
        var declaration = fixTarget?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            return null;
        }

        return [Location.Create(declaration.SyntaxTree, declaration.Span)];
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

        IdInfo(IdState state, ImmutableArray<string> tags)
        {
            State = state;
            Tags = tags;
        }

        public static IdInfo Unknown { get; } = new(IdState.Unknown, ImmutableArray<string>.Empty);
        public static IdInfo NotPresent { get; } = new(IdState.NotPresent, ImmutableArray<string>.Empty);

        public static IdInfo Present(string tag) =>
            new(IdState.Present, ImmutableArray.Create(tag));

        public static IdInfo Present(ImmutableArray<string> tags) =>
            tags.IsDefaultOrEmpty
                ? NotPresent
                : new(IdState.Present, tags);

        // Single-value accessor for the fixer (which needs one string to write back).
        // Picks the first tag — callers that care about multi-tag must use Tags directly.
        public string? FirstValue => Tags.IsDefaultOrEmpty ? null : Tags[0];

        // Every tag the target demands must be present in the source's tag set.
        // Used for assignment-like flow (argument → parameter, RHS → LHS).
        public bool SatisfiedBy(IdInfo source)
        {
            foreach (var tag in Tags)
            {
                if (!source.Tags.Contains(tag))
                {
                    return false;
                }
            }
            return true;
        }

        // Symmetric set intersection. Used for equality comparisons where either side
        // could be the identity the other is checking against.
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
