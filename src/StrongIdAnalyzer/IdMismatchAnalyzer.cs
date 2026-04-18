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
        });
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
