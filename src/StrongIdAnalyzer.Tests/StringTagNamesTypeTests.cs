public class StringTagNamesTypeTests
{
    [Test]
    public async Task StringTagNamingType_Fires()
    {
        var source =
            """
            using System.Linq;

            public class ConfigEntry
            {
                public string Id { get; set; }
            }

            public static class Queries
            {
                public static string Find(ConfigEntry[] entries, [Id("ConfigEntry")] string wellKnownConfigEntry) =>
                    entries.Where(_ => _.Id == wellKnownConfigEntry).Select(_ => _.Id).Single();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        var diagnostic = diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("SIA009");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "[Id(\"ConfigEntry\")] on parameter 'wellKnownConfigEntry' of 'Queries.Find' names the type 'ConfigEntry'. Fix: replace it with [Id<ConfigEntry>].");
    }

    [Test]
    public async Task NameofTag_Fires()
    {
        var source =
            """
            using System;

            public class Customer;

            public class Holder
            {
                [Id(nameof(Customer))]
                public Guid Owner { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA009"]);
    }

    [Test]
    public async Task FieldAndReturn_Fire()
    {
        var source =
            """
            using System;

            public class Customer;

            public class Holder
            {
                [Id("Customer")]
                Guid owner;

                [return: Id("Customer")]
                public Guid Get() => owner;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA009", "SIA009"]);
    }

    [Test]
    public async Task NoTypeOfThatName_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Warehouse")]
                public Guid Owner { get; set; }
            }
            """;

        await Assert.That(await GetDiagnostics(source)).IsEmpty();
    }

    [Test]
    public async Task GenericForm_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Customer;

            public class Holder
            {
                [Id<Customer>]
                public Guid Owner { get; set; }
            }
            """;

        await Assert.That(await GetDiagnostics(source)).IsEmpty();
    }

    // `[Id<Ledger>]` for `Ledger<TKey>` is CS0305 and a static class cannot be a type
    // argument (CS0718): neither has a generic form to move to.
    [Test]
    public async Task GenericOrStaticType_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Ledger<TKey>;

            public static class Catalog;

            public class Holder
            {
                [Id("Ledger")]
                public Guid LedgerKey { get; set; }

                [Id("Catalog")]
                public Guid CatalogKey { get; set; }
            }
            """;

        await Assert.That(await GetDiagnostics(source)).IsEmpty();
    }

    [Test]
    public async Task TypeNotInScope_NoDiagnostic()
    {
        var source =
            """
            using System;

            namespace Domain
            {
                public class Warehouse;
            }

            namespace App
            {
                public class Holder
                {
                    [Id("Warehouse")]
                    public Guid Owner { get; set; }
                }
            }
            """;

        await Assert.That(await GetDiagnostics(source)).IsEmpty();
    }

    // The generic attribute is only emitted from C# 11, so there is nothing to move to.
    [Test]
    public async Task BeforeCSharp11_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Customer
            {
            }

            public class Holder
            {
                [Id("Customer")]
                public Guid Owner { get; set; }
            }
            """;

        await Assert.That(await GetDiagnostics(source, LanguageVersion.CSharp10)).IsEmpty();
    }

    static async Task<ImmutableArray<Diagnostic>> GetDiagnostics(
        string source,
        LanguageVersion version = LanguageVersion.Latest)
    {
        var parseOptions = new CSharpParseOptions(version);
        var compilation = CSharpCompilation.Create(
            "Tests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create([new IdAttributeGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics().Where(_ => _.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new(string.Join("\n", errors.Select(_ => _.ToString())));
        }

        // Only this rule: the snippets are chosen to exercise it, not the flow rules.
        var diagnostics = await updated
            .WithAnalyzers([new IdMismatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [..diagnostics.Where(_ => _.Id == "SIA009")];
    }
}
