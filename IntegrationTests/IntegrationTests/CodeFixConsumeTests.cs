using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

[TestFixture]
public class CodeFixConsumeTests
{
    const string IdAttributeSource = """
        namespace StrongIdAnalyzer;

        using System;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            AllowMultiple = false,
            Inherited = false)]
        internal sealed class IdAttribute(string type) : Attribute
        {
            public string Type { get; } = type;
        }
        """;

    // Verifies that the Release-built nuget (from ../nugets) actually ships a loadable
    // CodeFixes.dll under analyzers/dotnet/cs/, and that the provider applies correctly
    // when driven from that DLL — not just from the codefix project reference used by
    // the src/ unit tests.
    [Test]
    public async Task PackagedCodeFixProvider_AddsIdAttribute_ForSIA002()
    {
        var (analyzer, codeFix) = LoadAnalyzerAndCodeFixFromPackage();

        var source = """
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

        var (document, diagnostic) = await CompileAndAnalyze(source, analyzer);

        AreEqual("SIA002", diagnostic.Id);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context);

        var action = actions.ToImmutable().Single();
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var apply = operations.OfType<ApplyChangesOperation>().Single();
        var newDoc = apply.ChangedSolution.GetDocument(document.Id)!;
        var fixedText = (await newDoc.GetTextAsync()).ToString();

        IsTrue(
            fixedText.Contains("[Id(\"Order\")]"),
            $"Expected fix to add [Id(\"Order\")] but got:\n{fixedText}");
    }

    static (DiagnosticAnalyzer Analyzer, CodeFixProvider CodeFix) LoadAnalyzerAndCodeFixFromPackage()
    {
        var analyzersDir = FindAnalyzersDirectory();

        var analyzerPath = Path.Combine(analyzersDir, "StrongIdAnalyzer.dll");
        var codeFixPath = Path.Combine(analyzersDir, "StrongIdAnalyzer.CodeFixes.dll");

        IsTrue(File.Exists(analyzerPath), $"Analyzer DLL missing: {analyzerPath}");
        IsTrue(File.Exists(codeFixPath), $"CodeFix DLL missing: {codeFixPath}");

        var analyzerAsm = Assembly.LoadFrom(analyzerPath);
        var codeFixAsm = Assembly.LoadFrom(codeFixPath);

        var analyzerType = analyzerAsm.GetType("StrongIdAnalyzer.IdMismatchAnalyzer");
        var codeFixType = codeFixAsm.GetType("StrongIdAnalyzer.AddIdCodeFixProvider");

        IsNotNull(analyzerType);
        IsNotNull(codeFixType);

        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType!)!;
        var codeFix = (CodeFixProvider)Activator.CreateInstance(codeFixType!)!;

        return (analyzer, codeFix);
    }

    static string FindAnalyzersDirectory()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");

        // AnalyzerPackageVersion is injected by the csproj via AssemblyMetadata, pinned to
        // $(Version). Scanning the package folder by name isn't enough — stale versions from
        // earlier local builds could sort above the current one lexically.
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(_ => _.Key == "AnalyzerPackageVersion")
            .Value!;

        var versionDir = Path.Combine(root, "strongidanalyzer", version);
        IsTrue(Directory.Exists(versionDir), $"Expected package directory missing: {versionDir}");

        return Path.Combine(versionDir, "analyzers", "dotnet", "cs");
    }

    static async Task<(Document Document, Diagnostic Diagnostic)> CompileAndAnalyze(
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

        return (document, diagnostics.Single());
    }

    static IEnumerable<MetadataReference> TrustedReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
}
