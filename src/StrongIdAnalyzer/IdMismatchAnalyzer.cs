namespace StrongIdAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IdMismatchAnalyzer : DiagnosticAnalyzer
{
    public const string ValueKey = "IdValue";

    // .editorconfig key for overriding the default namespace suppression list.
    // Value is comma-separated; trailing `*` means prefix match (e.g. `System*` matches
    // `System`, `System.Collections`, etc.). Setting an empty value disables suppression.
    internal const string SuppressedNamespacesOption = "strongidanalyzer.suppressed_namespaces";

    // Library namespaces whose members we can't realistically tag. Noise for SIA002/SIA003
    // when a tagged id flows into BCL / framework APIs (e.g. logging, serialization,
    // dependency injection, Entity Framework). Users can override via .editorconfig.
    static readonly ImmutableArray<string> DefaultSuppressedNamespaces =
        ["System*", "Microsoft*"];

    public static readonly DiagnosticDescriptor IdMismatchRule = new(
        id: "SIA001",
        title: "Id type mismatch",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target with [Id(\"{1}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingSourceIdRule = new(
        id: "SIA002",
        title: "Source has no Id while target requires one",
        messageFormat: "Value has no [Id] attribute but is assigned to a target with [Id(\"{0}\")]",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DroppedIdRule = new(
        id: "SIA003",
        title: "Source has Id while target has none",
        messageFormat: "Value with [Id(\"{0}\")] is assigned to a target without an [Id] attribute",
        category: "IdAttribute.Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [IdMismatchRule, MissingSourceIdRule, DroppedIdRule];

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
        });
    }

    static ImmutableArray<string> ReadSuppressedNamespaces(AnalyzerConfigOptionsProvider options)
    {
        if (!options.GlobalOptions.TryGetValue(SuppressedNamespacesOption, out var raw))
        {
            return DefaultSuppressedNamespaces;
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
        var leftInfo = GetId(leftSymbol, idType);
        var rightInfo = GetId(rightSymbol, idType);

        // Both tagged with different values: SIA001.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.Present)
        {
            if (string.Equals(leftInfo.Value, rightInfo.Value, StringComparison.Ordinal))
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                IdMismatchRule,
                operation.Syntax.GetLocation(),
                leftInfo.Value ?? "",
                rightInfo.Value ?? ""));
            return;
        }

        // One side tagged, other is a user-owned member with no tag: SIA002 on the missing
        // side so the fixer can add [Id("<same value>")] to its declaration. When the
        // untagged side is Unknown (literal, Guid.Empty, local var, method call) no
        // diagnostic fires — those are routine and not a bug.
        if (leftInfo.State == IdState.Present &&
            rightInfo.State == IdState.NotPresent)
        {
            ReportMissingOnBinarySide(context, operation.RightOperand, rightSymbol, leftInfo.Value, config);
            return;
        }

        if (leftInfo.State == IdState.NotPresent &&
            rightInfo.State == IdState.Present)
        {
            ReportMissingOnBinarySide(context, operation.LeftOperand, leftSymbol, rightInfo.Value, config);
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
            MissingSourceIdRule,
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
        var sourceInfo = GetId(sourceSymbol, idType);
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

        var targetInfo = GetId(targetSymbol, idType);
        var sourceSymbol = GetSymbol(assignment.Value);
        var sourceInfo = GetId(sourceSymbol, idType);
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
        var sourceInfo = GetId(sourceSymbol, idType);
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
        var sourceInfo = GetId(sourceSymbol, idType);
        foreach (var field in init.InitializedFields)
        {
            var targetInfo = GetIdFromAttributes(field.GetAttributes(), idType);
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
    static IdInfo GetIdWithInheritance(ISymbol symbol, INamedTypeSymbol idType)
    {
        var direct = GetIdFromAttributes(symbol.GetAttributes(), idType);
        if (direct.State == IdState.Present)
        {
            return direct;
        }

        if (symbol is IPropertySymbol property)
        {
            return GetPropertyIdFromHierarchy(property, idType);
        }

        if (symbol is IParameterSymbol parameter)
        {
            return GetParameterIdFromHierarchy(parameter, idType);
        }

        return direct;
    }

    static IdInfo GetParameterIdFromHierarchy(IParameterSymbol parameter, INamedTypeSymbol idType)
    {
        if (parameter.ContainingSymbol is not IMethodSymbol method)
        {
            return new(IdState.NotPresent, null);
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
            return new(IdState.NotPresent, null);
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

        return new(IdState.NotPresent, null);
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
            return new(IdState.NotPresent, null);
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

        return new(IdState.NotPresent, null);
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

            string? value = null;
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string s)
            {
                value = s;
            }

            return new(IdState.Present, value);
        }

        return new(IdState.NotPresent, null);
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
            if (!string.Equals(source.Value, target.Value, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    IdMismatchRule,
                    location,
                    source.Value ?? "",
                    target.Value ?? ""));
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
            context.ReportDiagnostic(CreateFixableDiagnostic(
                MissingSourceIdRule,
                location,
                sourceSymbol,
                target.Value));
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
                DroppedIdRule,
                location,
                targetSymbol,
                source.Value));
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

    readonly struct IdInfo(IdState state, string? value)
    {
        public IdState State { get; } = state;
        public string? Value { get; } = value;

        public static IdInfo Unknown => new(IdState.Unknown, null);
    }
}
