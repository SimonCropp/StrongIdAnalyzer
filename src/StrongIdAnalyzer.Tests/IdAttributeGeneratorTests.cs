public class IdAttributeGeneratorTests
{
    [Test]
    public async Task EmitsIdAttribute()
    {
        var runResult = RunGenerator("public class Dummy {}");

        await Assert.That(runResult.Diagnostics.Length).IsEqualTo(0);

        var generated = runResult.GeneratedTrees
            .Single(_ => _.FilePath.EndsWith("IdAttribute.g.cs"));
        var text = generated.ToString();

        await Assert.That(text.Contains("sealed class IdAttribute")).IsTrue();
        await Assert.That(text.Contains("namespace StrongIdAnalyzer")).IsTrue();
    }

    [Test]
    public async Task ConsumerCodeUsingIdAttribute_Compiles()
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

        await Assert.That(genDiagnostics.Length).IsEqualTo(0);

        var errors = updated.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        await Assert.That(errors.Length).IsEqualTo(0);
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
