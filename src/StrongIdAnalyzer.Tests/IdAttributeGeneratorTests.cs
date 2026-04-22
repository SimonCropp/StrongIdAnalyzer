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
            using System;

            public class Target
            {
                public static void Consume([Id("Order")] Guid value) { }
            }

            public class Holder
            {
                [Id("Order")]
                public Guid Value { get; set; }

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

    [Test]
    public async Task SkipsEmit_WhenAttributeVisibleFromReference()
    {
        // Simulate an upstream assembly that already has StrongIdAnalyzer.IdAttribute
        // exposed (public here stands in for InternalsVisibleTo).
        var upstreamSource =
            """
            namespace StrongIdAnalyzer;
            using System;
            public sealed class IdAttribute(string type) : Attribute;
            """;
        var upstream = CSharpCompilation.Create(
            "Upstream",
            [CSharpSyntaxTree.ParseText(upstreamSource)],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var emitResult = upstream.Emit(peStream);
        await Assert.That(emitResult.Success).IsTrue();
        peStream.Position = 0;
        var upstreamRef = MetadataReference.CreateFromStream(peStream);

        var compilation = CSharpCompilation.Create(
            "Downstream",
            [CSharpSyntaxTree.ParseText("public class Dummy {}")],
            [.. TrustedReferences.All, upstreamRef],
            new(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        var runResult = driver.RunGenerators(compilation).GetRunResult();

        var generated = runResult.GeneratedTrees
            .Where(_ => _.FilePath.EndsWith("IdAttribute.g.cs"))
            .ToArray();
        await Assert.That(generated.Length).IsEqualTo(0);

        var globalUsings = runResult.GeneratedTrees
            .Single(_ => _.FilePath.EndsWith("IdAttributeGlobalUsings.g.cs"));
        await Assert.That(globalUsings.ToString().Contains("global using StrongIdAnalyzer"))
            .IsTrue();
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
