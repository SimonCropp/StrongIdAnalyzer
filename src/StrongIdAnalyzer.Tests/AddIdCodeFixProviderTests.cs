[TestFixture]
public class AddIdCodeFixProviderTests
{
    const string idAttributeSource =
        """
        namespace StrongIdAnalyzer;

        using System;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            AllowMultiple = false,
            Inherited = false)]
        internal sealed class IdAttribute : Attribute
        {
            public IdAttribute(string type) => Type = type;
            public string Type { get; }
        }
        """;

    [Test]
    public async Task SIA002_AddsAttributeToSourceProperty()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Target
            {
                public void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                public System.Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"Order\")]");
        Contains(fixedSource, "public System.Guid Value { get; set; }");
    }

    [Test]
    public async Task SIA002_AddsAttributeToSourceField()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Holder
            {
                public System.Guid Field;

                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(Field);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"Order\")]");
        Contains(fixedSource, "public System.Guid Field;");
    }

    [Test]
    public async Task SIA002_AddsAttributeToSourceParameter()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Holder
            {
                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use(System.Guid input) => Consume(input);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"Order\")] System.Guid input");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetParameter()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Target
            {
                public static void Consume(System.Guid value) { }
            }

            public class Holder
            {
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                public void Use() => Target.Consume(OrderId);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"Order\")] System.Guid value");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetProperty()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Target
            {
                public System.Guid Value { get; set; }
            }

            public class Holder
            {
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                public void Use(Target target) => target.Value = OrderId;
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"Order\")]");
        Contains(fixedSource, "public System.Guid Value { get; set; }");
    }

    [Test]
    public async Task SIA002_CustomIdValue()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Holder
            {
                public System.Guid Value { get; set; }

                public static void Consume([Id("custom-tag")] System.Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id(\"custom-tag\")]");
    }

    [Test]
    public async Task MultiDeclaratorField_NoFixRegistered()
    {
        var source = """
            using StrongIdAnalyzer;

            public class Holder
            {
                public System.Guid a, b;

                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(a);
            }
            """;

        var actions = await GetCodeActions(source);

        AreEqual(0, actions.Length);
    }

    static void Contains(string actual, string expected) =>
        IsTrue(
            actual.Contains(expected),
            $"Expected fixed source to contain:\n{expected}\n\nActual:\n{actual}");

    static async Task<string> ApplyFix(string source)
    {
        var (document, diagnostic) = await PrepareFixAsync(source);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);

        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);

        var action = actions.ToImmutable().Single();
        var operations = await action.GetOperationsAsync(Cancel.None);
        var applyOperation = operations.OfType<ApplyChangesOperation>().Single();

        var newDocument = applyOperation.ChangedSolution.GetDocument(document.Id)!;
        var text = await newDocument.GetTextAsync();
        return text.ToString();
    }

    static async Task<ImmutableArray<CodeAction>> GetCodeActions(string source)
    {
        var (document, diagnostic) = await PrepareFixAsync(source);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);

        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);
        return actions.ToImmutable();
    }

    static async Task<(Document Document, Diagnostic Diagnostic)> PrepareFixAsync(string source)
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
            .AddDocument(idAttrId, "IdAttribute.cs", idAttributeSource)
            .AddDocument(documentId, "Test.cs", source);

        var document = solution.GetDocument(documentId)!;
        var compilation = (await document.Project.GetCompilationAsync())!;
        var diagnostics = await compilation
            .WithAnalyzers([new IdMismatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        return (document, diagnostics.Single());
    }

    static IEnumerable<MetadataReference> TrustedReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
}
