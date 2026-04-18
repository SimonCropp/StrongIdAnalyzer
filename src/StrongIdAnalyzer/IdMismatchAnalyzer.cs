namespace StrongIdAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IdMismatchAnalyzer : DiagnosticAnalyzer
{
    public const string ValueKey = "IdValue";

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

            start.RegisterOperationAction(
                _ => AnalyzeArgument(_, idType),
                OperationKind.Argument);
            start.RegisterOperationAction(
                _ => AnalyzeSimpleAssignment(_, idType),
                OperationKind.SimpleAssignment);
            start.RegisterOperationAction(
                _ => AnalyzePropertyInitializer(_, idType),
                OperationKind.PropertyInitializer);
            start.RegisterOperationAction(
                _ => AnalyzeFieldInitializer(_, idType),
                OperationKind.FieldInitializer);
            start.RegisterOperationAction(
                _ => AnalyzeBinaryOperator(_, idType),
                OperationKind.Binary);
        });
    }

    static void AnalyzeBinaryOperator(OperationAnalysisContext context, INamedTypeSymbol idType)
    {
        var op = (IBinaryOperation)context.Operation;
        if (op.OperatorKind != BinaryOperatorKind.Equals &&
            op.OperatorKind != BinaryOperatorKind.NotEquals)
        {
            return;
        }

        var leftSymbol = GetSymbol(op.LeftOperand);
        var rightSymbol = GetSymbol(op.RightOperand);
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
                op.Syntax.GetLocation(),
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
            ReportMissingOnBinarySide(context, op.RightOperand, rightSymbol, leftInfo.Value);
            return;
        }

        if (leftInfo.State == IdState.NotPresent &&
            rightInfo.State == IdState.Present)
        {
            ReportMissingOnBinarySide(context, op.LeftOperand, leftSymbol, rightInfo.Value);
        }
    }

    static void ReportMissingOnBinarySide(
        OperationAnalysisContext context,
        IOperation untaggedOperand,
        ISymbol? untaggedSymbol,
        string? tag)
    {
        // Only fix user-owned declarations. Library members (Guid.Empty etc.) can't carry
        // [Id] so firing here would just be noise.
        if (untaggedSymbol is null ||
            untaggedSymbol.DeclaringSyntaxReferences.IsEmpty)
        {
            return;
        }

        context.ReportDiagnostic(CreateFixableDiagnostic(
            MissingSourceIdRule,
            untaggedOperand.Syntax.GetLocation(),
            untaggedSymbol,
            tag));
    }

    static void AnalyzeArgument(OperationAnalysisContext context, INamedTypeSymbol idType)
    {
        var argument = (IArgumentOperation)context.Operation;
        var parameter = argument.Parameter;
        if (parameter is null)
        {
            return;
        }

        var targetInfo = GetIdFromAttributes(parameter.GetAttributes(), idType);
        var sourceSymbol = GetSymbol(argument.Value);
        var sourceInfo = GetId(sourceSymbol, idType);
        Report(
            context,
            argument.Value.Syntax.GetLocation(),
            sourceSymbol,
            sourceInfo,
            parameter,
            targetInfo);
    }

    static void AnalyzeSimpleAssignment(OperationAnalysisContext context, INamedTypeSymbol idType)
    {
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
            targetInfo);
    }

    static void AnalyzePropertyInitializer(OperationAnalysisContext context, INamedTypeSymbol idType)
    {
        var init = (IPropertyInitializerOperation)context.Operation;
        var sourceSymbol = GetSymbol(init.Value);
        var sourceInfo = GetId(sourceSymbol, idType);
        foreach (var property in init.InitializedProperties)
        {
            var targetInfo = GetIdFromAttributes(property.GetAttributes(), idType);
            Report(
                context,
                init.Value.Syntax.GetLocation(),
                sourceSymbol,
                sourceInfo,
                property,
                targetInfo);
        }
    }

    static void AnalyzeFieldInitializer(OperationAnalysisContext context, INamedTypeSymbol idType)
    {
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
                targetInfo);
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

    static IdInfo GetId(ISymbol? symbol, INamedTypeSymbol idType) =>
        symbol is null
            ? IdInfo.Unknown
            : GetIdFromAttributes(symbol.GetAttributes(), idType);

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
        IdInfo target)
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

            // Fix site is the target symbol's declaration (add Id matching source).
            context.ReportDiagnostic(CreateFixableDiagnostic(
                DroppedIdRule,
                location,
                targetSymbol,
                source.Value));
        }
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
