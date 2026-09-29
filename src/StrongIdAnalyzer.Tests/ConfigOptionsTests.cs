// The .editorconfig keys are read per tree, so a tree outside the project's .editorconfig
// cannot hide them from the rest. TUnit 1.71+ injects TUnit.Core.GeneratedNamespace.cs
// from the NuGet cache ahead of the project's own files; that tree sits outside the
// project's .editorconfig; reading only the first tree silently dropped every setting.
// Each test here opens the compilation with such an out-of-scope tree.
public class ConfigOptionsTests
{
    const string outOfScope =
        """
        namespace TUnit.Generated;
        """;

    [Test]
    public async Task SuppressedNamespaces_ReadPastOutOfScopeFirstTree()
    {
        var source =
            """
            namespace Suppressed.Library
            {
                public static class SuppressedEndpoint
                {
                    public static void Save(System.Guid id) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Call() =>
                    Suppressed.Library.SuppressedEndpoint.Save(OrderId);
            }
            """;

        var diagnostics = await Run(
            source,
            new()
            {
                ["strongidanalyzer.suppressed_namespaces"] = "System*,Microsoft*,Suppressed*"
            });

        await Assert.That(diagnostics.Where(_ => _.Id == "SIA003")).IsEmpty();
    }

    [Test]
    public async Task InferSuffixIds_ReadPastOutOfScopeFirstTree()
    {
        var source =
            """
            public class Product
            {
                public System.Guid Id { get; set; }
            }

            public class Consumer
            {
                public static void Duplicate(System.Guid sourceProductId)
                {
                }

                public void Call(Product product) =>
                    Duplicate(product.Id);
            }
            """;

        var diagnostics = await Run(
            source,
            new()
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Where(_ => _.Id == "SIA001")).IsEmpty();
    }

    [Test]
    public async Task InferWrapperIds_ReadPastOutOfScopeFirstTree()
    {
        var source =
            """
            public readonly record struct UserId(System.Guid Value);

            public class Parents
            {
                public UserId momUserId;
                public UserId dadUserId;

                public void Swap() =>
                    momUserId = dadUserId;
            }
            """;

        var diagnostics = await Run(
            source,
            new()
            {
                ["strongidanalyzer.infer_wrapper_ids"] = "true"
            });

        await Assert.That(diagnostics.Where(_ => _.Id == "SIA001")).IsEmpty();
    }

    // Guard for the tests above: with the out-of-scope tree first and no key anywhere,
    // the diagnostic fires — so the empty results come from the key being read.
    [Test]
    public async Task WithoutKey_DiagnosticFires()
    {
        var source =
            """
            public readonly record struct UserId(System.Guid Value);

            public class Parents
            {
                public UserId momUserId;
                public UserId dadUserId;

                public void Swap() =>
                    momUserId = dadUserId;
            }
            """;

        var diagnostics = await Run(source, []);

        await Assert.That(diagnostics.Select(_ => _.Id)).Contains("SIA001");
    }

    // Options are per tree: a folder whose .editorconfig enables wrappers gets them, a
    // sibling folder without it does not, in the same compilation.
    [Test]
    public async Task OptionsApplyPerTree()
    {
        var wrapper =
            """
            public readonly record struct UserId(System.Guid Value);
            """;
        var parents =
            """
            public class Parents{0}
            {{
                public UserId momUserId;
                public UserId dadUserId;

                public void Swap() =>
                    momUserId = dadUserId;
            }}
            """;

        var diagnostics = await Run(
            [
                ("/project/UserId.cs", wrapper),
                ("/project/enabled/Parents.cs", string.Format(parents, "Enabled")),
                ("/project/disabled/Parents.cs", string.Format(parents, "Disabled"))
            ],
            new()
            {
                ["/project/enabled/"] = new()
                {
                    ["strongidanalyzer.infer_wrapper_ids"] = "true"
                }
            });

        var paths = diagnostics
            .Where(_ => _.Id == "SIA001")
            .Select(_ => _.Location.SourceTree!.FilePath)
            .Distinct();
        await Assert.That(paths).IsEquivalentTo(["/project/disabled/Parents.cs"]);
    }

    static Task<ImmutableArray<Diagnostic>> Run(string source, Dictionary<string, string> options) =>
        Run(
            [
                ("/nuget/tunit.core/TUnit.Core.GeneratedNamespace.cs", outOfScope),
                ("/project/Sample.cs", source)
            ],
            new()
            {
                ["/project/"] = options
            });

    static Task<ImmutableArray<Diagnostic>> Run(
        (string Path, string Source)[] files,
        Dictionary<string, Dictionary<string, string>> optionsByFolder)
    {
        var compilation = CSharpCompilation.Create(
            "Tests",
            files.Select(_ => CSharpSyntaxTree.ParseText(_.Source, path: _.Path)),
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics().Where(_ => _.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new(string.Join(Environment.NewLine, errors.Select(_ => _.ToString())));
        }

        var analyzerOptions = new AnalyzerOptions([], new ScopedOptionsProvider(optionsByFolder));
        return updated
            .SuppressStringTagHint()
            .WithAnalyzers([new IdMismatchAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    // Mirrors per-folder .editorconfig files: a tree gets the options of the folder it sits
    // under, every other tree (and GlobalOptions, as `[*.cs]` entries never reach it) gets
    // nothing.
    sealed class ScopedOptionsProvider(Dictionary<string, Dictionary<string, string>> optionsByFolder) :
        AnalyzerConfigOptionsProvider
    {
        readonly TestAnalyzerConfigOptions empty = new(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions => empty;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
        {
            foreach (var (folder, options) in optionsByFolder)
            {
                if (tree.FilePath.StartsWith(folder, StringComparison.Ordinal))
                {
                    return new TestAnalyzerConfigOptions(options);
                }
            }

            return empty;
        }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => empty;
    }
}
