namespace StrongIdAnalyzer.Benchmarks;

// Measures analyzer cost on a consumer compilation that references a library with
// non-trivial inheritance structure. Each call site in the consumer does
// `concrete.Id` property access plus a method call with a [Id]-tagged parameter —
// exercising EnumerateMemberChain (AllInterfaces + FindImplementationForInterfaceMember)
// on the source side. Compare against the indexed variant to see whether skipping
// that walk via a producer-emitted index attribute is worth the engineering cost.
[MemoryDiagnoser]
public class CrossAssemblyAnalyzerBenchmarks
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

        var libraryTree = CSharpSyntaxTree.ParseText(SourceBuilder.LibrarySource);
        var libraryCompilation = CSharpCompilation.Create(
            "BenchLib",
            [libraryTree],
            tpaReferences,
            new(OutputKind.DynamicallyLinkedLibrary));

        // Emit to a byte[] and reference via CreateFromImage so the consumer compilation
        // sees Customer/Order/Methods as metadata symbols, not source symbols. This is
        // the path a real consumer build takes against a NuGet-referenced library.
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

    // Baseline: compiler work with no analyzer attached. Subtracting from Analyze
    // shows the marginal cost the analyzer itself adds.
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
