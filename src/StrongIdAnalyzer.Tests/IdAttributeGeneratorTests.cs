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
        await Assert.That(text.Contains("sealed class ExternalIdAttribute")).IsTrue();
        await Assert.That(text.Contains("namespace StrongIdAnalyzer")).IsTrue();
    }

    [Test]
    public async Task ConsumerCodeUsingExternalIdAttribute_Compiles()
    {
        var source =
            """
            using System.Diagnostics;

            [assembly: ExternalId(typeof(Process), nameof(Process.Id), "Process")]
            [assembly: ExternalId(typeof(Process), nameof(Process.SessionId), "Session", "LogonSession")]

            public class Holder
            {
                public int ProcessId { get; set; }

                public void Use(Process process) => ProcessId = process.Id;
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

    // The generated source is parsed at the CONSUMER's language version. A netstandard2.0
    // or .NET Framework project defaults to C# 7.3, where a file-scoped namespace, a
    // `global using` and a primary constructor are all errors — so referencing the
    // analyzer at all stopped such a project from compiling. The declarations are written
    // in syntax every compiler accepts; only the generic attribute forms (C# 11) and the
    // `global using` (C# 10) are gated.
    [Test]
    [Arguments(LanguageVersion.CSharp7_3)]
    [Arguments(LanguageVersion.CSharp8)]
    [Arguments(LanguageVersion.CSharp9)]
    [Arguments(LanguageVersion.CSharp10)]
    [Arguments(LanguageVersion.CSharp11)]
    [Arguments(LanguageVersion.CSharp12)]
    public async Task GeneratedAttributes_CompileOnEveryLanguageVersion(LanguageVersion version)
    {
        var source = version >= LanguageVersion.CSharp10
            ? "public class Dummy { }"
            : """
              using StrongIdAnalyzer;

              public class Dummy { }
              """;

        var compilation = BuildCompilation(source, version);
        var parseOptions = new CSharpParseOptions(version);
        var driver = CSharpGeneratorDriver.Create(
            [new IdAttributeGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics);

        await Assert.That(generatorDiagnostics.Length).IsEqualTo(0);

        var errors = updated.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        await Assert.That(string.Join("\n", errors.Select(_ => _.ToString()))).IsEqualTo("");
    }

    [Test]
    public async Task GlobalUsing_OnlyEmittedFromCSharp10()
    {
        var below = RunGenerator("public class Dummy { }", LanguageVersion.CSharp9);
        await Assert.That(below.GeneratedTrees.Any(_ => _.FilePath.EndsWith("IdAttributeGlobalUsings.g.cs")))
            .IsFalse();

        var above = RunGenerator("public class Dummy { }", LanguageVersion.CSharp10);
        await Assert.That(above.GeneratedTrees.Any(_ => _.FilePath.EndsWith("IdAttributeGlobalUsings.g.cs")))
            .IsTrue();
    }

    [Test]
    public async Task GenericForms_OnlyEmittedFromCSharp11()
    {
        var below = RunGenerator("public class Dummy { }", LanguageVersion.CSharp10);
        var belowText = below.GeneratedTrees
            .Single(_ => _.FilePath.EndsWith("IdAttribute.g.cs"))
            .ToString();
        await Assert.That(belowText.Contains("class IdAttribute<T>")).IsFalse();

        var above = RunGenerator("public class Dummy { }", LanguageVersion.CSharp11);
        var aboveText = above.GeneratedTrees
            .Single(_ => _.FilePath.EndsWith("IdAttribute.g.cs"))
            .ToString();
        await Assert.That(aboveText.Contains("class IdAttribute<T>")).IsTrue();
    }

    static GeneratorDriverRunResult RunGenerator(string source) =>
        RunGenerator(source, LanguageVersion.Latest);

    static GeneratorDriverRunResult RunGenerator(string source, LanguageVersion version)
    {
        var compilation = BuildCompilation(source, version);

        var driver = CSharpGeneratorDriver.Create(
            [new IdAttributeGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(version));
        return driver.RunGenerators(compilation).GetRunResult();
    }

    static CSharpCompilation BuildCompilation(string source) =>
        BuildCompilation(source, LanguageVersion.Latest);

    static CSharpCompilation BuildCompilation(string source, LanguageVersion version) =>
        CSharpCompilation.Create(
            "Tests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version))],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));
}
