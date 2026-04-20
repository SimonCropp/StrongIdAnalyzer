[TestFixture]
public class IdAttributeGeneratorTests
{
    [Test]
    public void EmitsIdAttribute()
    {
        var runResult = RunGenerator("public class Dummy {}");

        AreEqual(0, runResult.Diagnostics.Length);

        var generated = runResult.GeneratedTrees
            .Single(_ => _.FilePath.EndsWith("IdAttribute.g.cs"));
        var text = generated.ToString();

        IsTrue(text.Contains("sealed class IdAttribute"));
        IsTrue(text.Contains("namespace StrongIdAnalyzer"));
    }

    [Test]
    public void ConsumerCodeUsingIdAttribute_Compiles()
    {
        var source =
            """
            public class Target
            {
                public static void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                [Id("Order")]
                public System.Guid Value { get; set; }

                public void Use() => Target.Consume(Value);
            }
            """;

        var compilation = BuildCompilation(source);
        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var genDiagnostics);

        AreEqual(0, genDiagnostics.Length);

        var errors = updated.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        AreEqual(
            0,
            errors.Length,
            string.Join("\n", errors.Select(_ => _.ToString())));
    }

    static GeneratorDriverRunResult RunGenerator(string source)
    {
        var compilation = BuildCompilation(source);

        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    static CSharpCompilation BuildCompilation(string source) =>
        CSharpCompilation.Create(
            "Tests",
            [CSharpSyntaxTree.ParseText(source)],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));
}
