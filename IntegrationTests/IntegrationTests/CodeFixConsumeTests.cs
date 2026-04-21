using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
// TUnit introduces `global using static TUnit.Core.HookType;` which defines an
// `Assembly` member; collide-proof by keeping `System.Reflection.Assembly` fully
// qualified at use sites.
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

public class CodeFixConsumeTests
{
    const string IdAttributeSource =
        """
        namespace StrongIdAnalyzer;

        using System;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            Inherited = false)]
        internal sealed class IdAttribute(string type) : Attribute;
        """;

    const string Sia002Source =
        """
        using StrongIdAnalyzer;

        public class Target
        {
            public static void Consume([Id("Order")] System.Guid value) { }
        }

        public class Holder
        {
            public System.Guid Value { get; set; }

            public void Use() => Target.Consume(Value);
        }
        """;

    // Verifies that the Release-built nuget (from ../nugets) actually ships a loadable
    // CodeFixes.dll under analyzers/dotnet/cs/, and that the provider applies correctly
    // when driven from that DLL — not just from the codefix project reference used by
    // the src/ unit tests.
    [Test]
    public async Task PackagedCodeFixProvider_AddsIdAttribute_ForSIA002()
    {
        var (analyzer, codeFix) = await LoadAnalyzerAndCodeFixFromPackage();

        var (document, diagnostic) = await CompileAndAnalyzeSingle(Sia002Source, analyzer);

        await Assert.That(diagnostic.Id).IsEqualTo("SIA002");

        var actions = await RegisterActions(codeFix, document, diagnostic);

        var action = actions.Single(_ => _.EquivalenceKey!.StartsWith("AddId:"));
        var fixedText = await ApplyAction(action, document);

        await Assert.That(fixedText.Contains("[Id(\"Order\")]")).IsTrue();
    }

    // Rename-to-convention action was added after AddId; this asserts the packaged
    // CodeFixes DLL ships the newer action alongside the original — a release that
    // drops it would silently regress.
    [Test]
    public async Task PackagedCodeFixProvider_OffersRenameToConvention_ForSIA002()
    {
        var (analyzer, codeFix) = await LoadAnalyzerAndCodeFixFromPackage();

        var (document, diagnostic) = await CompileAndAnalyzeSingle(Sia002Source, analyzer);

        var actions = await RegisterActions(codeFix, document, diagnostic);

        var rename = actions.Single(_ => _.EquivalenceKey!.StartsWith("RenameId:"));
        var fixedText = await ApplyAction(rename, document);

        await Assert.That(fixedText.Contains("OrderId")).IsTrue();
    }

    // Reflect over every CodeFixProvider type in the packaged DLL and instantiate it.
    // Catches the case where a new provider is added in src/ but not exported with a
    // parameterless ctor or a public visibility in the shipped package.
    [Test]
    public async Task PackagedCodeFixesAssembly_AllProvidersInstantiate()
    {
        var analyzersDir = await FindAnalyzersDirectory();
        var codeFixAsm = System.Reflection.Assembly.LoadFrom(Path.Combine(analyzersDir, "StrongIdAnalyzer.CodeFixes.dll"));

        // GetTypes throws ReflectionTypeLoadException when a transitive dependency
        // (e.g. Microsoft.Bcl.AsyncInterfaces) isn't resolvable from the test's
        // bin directory. Codefix providers themselves don't need those transitives,
        // so fall back to the subset of types that did load.
        Type[] loaded;
        try
        {
            loaded = codeFixAsm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            loaded = ex.Types.Where(_ => _ != null).ToArray()!;
        }

        var providerTypes = loaded
            .Where(_ => !_.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(_))
            .ToArray();

        await Assert.That(providerTypes).IsNotEmpty();

        foreach (var type in providerTypes)
        {
            var provider = (CodeFixProvider)Activator.CreateInstance(type)!;
            await Assert.That(provider.FixableDiagnosticIds).IsNotEmpty();
        }
    }

    // End-to-end proof the packaged analyzer DLL actually raises SIA001 on a crossed
    // assignment. The other tests all start from a clean build; this one closes the loop
    // on diagnostic production itself.
    [Test]
    public async Task PackagedAnalyzer_Raises_SIA001_On_Mismatch()
    {
        var (analyzer, _) = await LoadAnalyzerAndCodeFixFromPackage();

        var source =
            """
            using StrongIdAnalyzer;

            public class Target
            {
                public static void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                // CustomerId picks up "Customer" by naming convention — an explicit
                // [Id("Customer")] would redundantly also raise SIA005.
                public System.Guid CustomerId { get; set; }

                public void Use() => Target.Consume(CustomerId);
            }
            """;

        var (_, diagnostics) = await CompileAndAnalyze(source, analyzer);

        await Assert.That(diagnostics.Any(_ => _.Id == "SIA001")).IsTrue();
    }

    static async Task<ImmutableArray<CodeAction>> RegisterActions(
        CodeFixProvider codeFix,
        Document document,
        Diagnostic diagnostic)
    {
        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context);
        return actions.ToImmutable();
    }

    static async Task<string> ApplyAction(CodeAction action, Document document)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        var newDoc = apply.ChangedSolution.GetDocument(document.Id)!;
        return (await newDoc.GetTextAsync()).ToString();
    }

    static async Task<(DiagnosticAnalyzer Analyzer, CodeFixProvider CodeFix)> LoadAnalyzerAndCodeFixFromPackage()
    {
        var analyzersDir = await FindAnalyzersDirectory();

        var analyzerPath = Path.Combine(analyzersDir, "StrongIdAnalyzer.dll");
        var codeFixPath = Path.Combine(analyzersDir, "StrongIdAnalyzer.CodeFixes.dll");

        await Assert.That(File.Exists(analyzerPath)).IsTrue();
        await Assert.That(File.Exists(codeFixPath)).IsTrue();

        var analyzerAsm = System.Reflection.Assembly.LoadFrom(analyzerPath);
        var codeFixAsm = System.Reflection.Assembly.LoadFrom(codeFixPath);

        var analyzerType = analyzerAsm.GetType("StrongIdAnalyzer.IdMismatchAnalyzer");
        var codeFixType = codeFixAsm.GetType("StrongIdAnalyzer.AddIdCodeFixProvider");

        await Assert.That(analyzerType).IsNotNull();
        await Assert.That(codeFixType).IsNotNull();

        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType!)!;
        var codeFix = (CodeFixProvider)Activator.CreateInstance(codeFixType!)!;

        return (analyzer, codeFix);
    }

    static async Task<string> FindAnalyzersDirectory()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");

        // AnalyzerPackageVersion is injected by the csproj via AssemblyMetadata, pinned to
        // $(Version). Scanning the package folder by name isn't enough — stale versions from
        // earlier local builds could sort above the current one lexically.
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .Single(_ => _.Key == "AnalyzerPackageVersion")
            .Value!;

        var versionDir = Path.Combine(root, "strongidanalyzer", version);
        await Assert.That(Directory.Exists(versionDir)).IsTrue();

        return Path.Combine(versionDir, "analyzers", "dotnet", "cs");
    }

    static async Task<(Document Document, Diagnostic Diagnostic)> CompileAndAnalyzeSingle(
        string source,
        DiagnosticAnalyzer analyzer)
    {
        var (document, diagnostics) = await CompileAndAnalyze(source, analyzer);
        return (document, diagnostics.Single());
    }

    static async Task<(Document Document, ImmutableArray<Diagnostic> Diagnostics)> CompileAndAnalyze(
        string source,
        DiagnosticAnalyzer analyzer)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "Tests",
            assemblyName: "Tests",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: TrustedReferences());

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        var idAttrId = DocumentId.CreateNewId(projectInfo.Id);
        var documentId = DocumentId.CreateNewId(projectInfo.Id);
        solution = solution
            .AddDocument(idAttrId, "IdAttribute.cs", IdAttributeSource)
            .AddDocument(documentId, "Test.cs", source);

        var document = solution.GetDocument(documentId)!;
        var compilation = (await document.Project.GetCompilationAsync())!;
        var diagnostics = await compilation
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync();

        return (document, diagnostics);
    }

    static IEnumerable<MetadataReference> TrustedReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
}
