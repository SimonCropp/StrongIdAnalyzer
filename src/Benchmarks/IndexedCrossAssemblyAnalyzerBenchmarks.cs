namespace StrongIdAnalyzer.Benchmarks;

// Mirrors CrossAssemblyAnalyzerBenchmarks but has the library ship an
// [assembly: StrongIdIndex(...)] enumerating the final tag sets for every tagged
// member. With the index present the analyzer skips EnumerateMemberChain,
// GetParameterIdFromHierarchy, and the receiver-type walk for library symbols.
[MemoryDiagnoser]
public class IndexedCrossAssemblyAnalyzerBenchmarks
{
    [Params(10, 100, 500)]
    public int CallSites;

    CSharpCompilation compilation = null!;
    ImmutableArray<DiagnosticAnalyzer> analyzers;

    [GlobalSetup]
    public void Setup()
    {
        var tpaReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(_ => (MetadataReference)MetadataReference.CreateFromFile(_))
            .ToArray();

        // Two syntax trees so the assembly-attribute tree can legally place its
        // [assembly: ...] application after `using` clauses and before all namespace
        // declarations — C# rejects that ordering when smashed into one file with the
        // library types. StrongIdIndexAttribute is internal per-assembly, mirroring the
        // Id/UnionId attribute pattern. Encoding: "DocId=Tag1,Tag2;...", with parameter
        // DocIds as "MethodDocId::paramName" since Roslyn has no native parameter form.
        var indexTree = CSharpSyntaxTree.ParseText(
            """
            using System;

            [assembly: StrongIdAnalyzer.StrongIdIndexAttribute(
                "P:ICustomer.Id=Customer;" +
                "P:IOrder.Id=Order;" +
                "P:Customer.Id=Customer;" +
                "P:Order.Id=Order;" +
                "M:Methods.TakeCustomerId(System.Guid)::id=Customer;" +
                "M:Methods.TakeOrderId(System.Guid)::id=Order")]

            namespace StrongIdAnalyzer
            {
                [AttributeUsage(AttributeTargets.Assembly)]
                internal sealed class StrongIdIndexAttribute(string encoded) : Attribute
                {
                    public string Encoded { get; } = encoded;
                }
            }
            """);

        var libraryTree = CSharpSyntaxTree.ParseText(SourceBuilder.LibrarySource);

        var libraryCompilation = CSharpCompilation.Create(
            "BenchLibIndexed",
            [indexTree, libraryTree],
            tpaReferences,
            new(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emit = libraryCompilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Library compilation failed: " +
                string.Join(Environment.NewLine, emit.Diagnostics));
        }

        var libraryReference = MetadataReference.CreateFromImage(stream.ToArray());

        var consumerTree = CSharpSyntaxTree.ParseText(SourceBuilder.BuildConsumer(CallSites));

        compilation = CSharpCompilation.Create(
            "BenchConsumer",
            [consumerTree],
            [.. tpaReferences, libraryReference],
            new(OutputKind.DynamicallyLinkedLibrary));

        analyzers = [new IdMismatchAnalyzer()];
    }

    [Benchmark(Baseline = true)]
    public int CompileOnly() =>
        compilation.GetDiagnostics().Length;

    [Benchmark]
    public int Analyze() =>
        compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult()
            .Length;

}
