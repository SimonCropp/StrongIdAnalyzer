// Cross-assembly tests for the [assembly: StrongIdIndex] pre-resolution path. Each
// test builds a library compilation that carries the index attribute, emits it to an
// in-memory assembly, references it from a consumer compilation, and checks the
// analyzer's output. This is the only test shape that exercises TryGetFromIndex —
// LoadIndex early-outs for symbols defined in the source assembly.
[TestFixture]
public class IndexTests
{
    const string indexAttributeDeclaration =
        """
        namespace StrongIdAnalyzer
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            internal sealed class StrongIdIndexAttribute(string encoded) : System.Attribute
            {
                public string Encoded { get; } = encoded;
            }
        }
        """;

    // Library property `Vault.Token` is named neither `Id` nor `XxxId`, so no naming
    // convention applies. Without the index the analyzer returns NotPresent for it;
    // with the index it returns the indexed tag. The observable difference: a matching
    // target changes from "SIA002 suppressed for metadata source" (no diagnostic) to
    // "tags equal, no diagnostic" — same output, so this test uses a DIFFERENT tag in
    // the index to force a flip. See IndexEntry_MismatchWithTargetReportsSIA001 for
    // the matching-case shape; this one proves the NotPresent → Present transition.
    [Test]
    public void IndexEntry_SuppliesTagForOtherwiseUnknownMember()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public class Vault
            {
                public Guid Token { get; set; }
            }
            public static class Methods
            {
                public static void TakeCustomerId([StrongIdAnalyzer.Id("Customer")] Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Vault vault) => Methods.TakeCustomerId(vault.Token);
            }
            """;

        var withoutIndex = GetCrossAssemblyDiagnostics(library, index: null, consumer);
        Assert.That(withoutIndex, Is.Empty,
            "source is NotPresent and target is Present, but SIA002 is suppressed when " +
            "the source lives in referenced metadata (can't be fixed).");

        var withIndexOrder = GetCrossAssemblyDiagnostics(
            library,
            index: "P:Vault.Token=Order",
            consumer);
        Assert.That(withIndexOrder.Select(_ => _.Id), Is.EquivalentTo(["SIA001"]),
            "index gives Vault.Token the Order tag; target is Customer → SIA001 (index was read).");
    }

    // An index entry can flip the result from "no diagnostic" to "SIA001" by giving
    // the source a tag that conflicts with the target. Proves the index value is
    // actually consumed (not just that the lookup path runs).
    [Test]
    public void IndexEntry_MismatchWithTargetReportsSIA001()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public class Vault
            {
                public Guid Token { get; set; }
            }
            public static class Methods
            {
                public static void TakeCustomerId([StrongIdAnalyzer.Id("Customer")] Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Vault vault) => Methods.TakeCustomerId(vault.Token);
            }
            """;

        var diagnostics = GetCrossAssemblyDiagnostics(
            library,
            index: "P:Vault.Token=Order",
            consumer);

        Assert.That(diagnostics.Select(_ => _.Id), Is.EquivalentTo(["SIA001"]));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Order").And.Contain("Customer"));
    }

    // Comma-separated tag values decode to a multi-tag set — the analyzer treats it
    // like a UnionId. Target requiring any one of the options is satisfied.
    [Test]
    public void IndexEntry_MultipleTags_MatchesAnyOne()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public class Vault
            {
                public Guid Token { get; set; }
            }
            public static class Methods
            {
                public static void TakeCustomerId([StrongIdAnalyzer.Id("Customer")] Guid id) { }
                public static void TakeOrderId([StrongIdAnalyzer.Id("Order")] Guid id) { }
                public static void TakeUnrelatedId([StrongIdAnalyzer.Id("Invoice")] Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Vault vault)
                {
                    Methods.TakeCustomerId(vault.Token);   // matches "Customer"
                    Methods.TakeOrderId(vault.Token);      // matches "Order"
                    Methods.TakeUnrelatedId(vault.Token);  // no match — SIA001
                }
            }
            """;

        var diagnostics = GetCrossAssemblyDiagnostics(
            library,
            index: "P:Vault.Token=Customer,Order",
            consumer);

        Assert.That(diagnostics.Select(_ => _.Id), Is.EquivalentTo(["SIA001"]));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Invoice"));
    }

    // Parameter entries use a custom "M:MethodDocId::paramName" form — Roslyn has
    // no native parameter DocId. The analyzer decodes the `::` split in
    // ResolveIndexSymbol and looks up the matching parameter on the resolved method.
    [Test]
    public void IndexEntry_ParameterForm_ResolvesToParameter()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public class Vault
            {
                [StrongIdAnalyzer.Id("Customer")]
                public Guid CustomerToken { get; set; }
            }
            public static class Methods
            {
                // No [Id] on the parameter — only the index carries the tag.
                public static void Take(Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Vault vault) => Methods.Take(vault.CustomerToken);
            }
            """;

        var withoutIndex = GetCrossAssemblyDiagnostics(library, index: null, consumer);
        Assert.That(withoutIndex, Is.Empty,
            "no index + no [Id] on parameter → target Unknown, no diagnostic fires");

        var withIndex = GetCrossAssemblyDiagnostics(
            library,
            index: "M:Methods.Take(System.Guid)::id=Order",
            consumer);
        Assert.That(withIndex.Select(_ => _.Id), Is.EquivalentTo(["SIA001"]),
            "index gives parameter the Order tag; source is Customer → SIA001");
    }

    // The analyzer silently ignores entries whose DocIds don't resolve (typo,
    // wrong signature, removed member). Other entries in the same attribute still
    // load correctly. Guards against a stale index taking the analyzer down with it.
    [Test]
    public void IndexEntry_UnresolvableKeys_AreIgnored()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public class Vault
            {
                public Guid Token { get; set; }
            }
            public static class Methods
            {
                public static void TakeCustomerId([StrongIdAnalyzer.Id("Customer")] Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Vault vault) => Methods.TakeCustomerId(vault.Token);
            }
            """;

        var diagnostics = GetCrossAssemblyDiagnostics(
            library,
            // First entry is garbage (type doesn't exist); second is well-formed.
            index: "P:DoesNotExist.Missing=Whatever;P:Vault.Token=Customer",
            consumer);

        Assert.That(diagnostics, Is.Empty,
            "the garbage entry is skipped; the Vault.Token entry still loads and matches target");
    }

    // A referenced assembly with NO index attribute falls back to the existing walk —
    // backwards compatibility check. Naming convention is gated on source symbols, so
    // the library needs an explicit [Id] for the walk to have anything to report. The
    // inheritance walk picks it up from the interface member even though the concrete
    // class doesn't repeat the attribute.
    [Test]
    public void NoIndexAttribute_FallsBackToWalk()
    {
        var library =
            """
            using System;
            namespace StrongIdAnalyzer
            {
                [AttributeUsage(
                    AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
                    AllowMultiple = false, Inherited = false)]
                internal sealed class IdAttribute(string type) : Attribute;
            }
            public interface ICustomer
            {
                [StrongIdAnalyzer.Id("Customer")] Guid Id { get; }
            }
            public class Customer : ICustomer
            {
                public Guid Id { get; set; }
            }
            public static class Methods
            {
                public static void TakeOrderId([StrongIdAnalyzer.Id("Order")] Guid id) { }
            }
            """;

        var consumer =
            """
            public class CallSite
            {
                public void Run(Customer customer) => Methods.TakeOrderId(customer.Id);
            }
            """;

        var diagnostics = GetCrossAssemblyDiagnostics(library, index: null, consumer);

        Assert.That(diagnostics.Select(_ => _.Id), Is.EquivalentTo(["SIA001"]),
            "walk finds Customer tag on ICustomer.Id via AllInterfaces; target is Order → SIA001");
    }

    // Compiles `library` into an in-memory dll (optionally adding
    // [assembly: StrongIdIndexAttribute(...)]), then compiles `consumer` with that
    // dll as a metadata reference and runs the analyzer. Returns the analyzer's
    // diagnostics on the consumer only.
    static ImmutableArray<Diagnostic> GetCrossAssemblyDiagnostics(
        string library,
        string? index,
        string consumer)
    {
        // Strip the test assembly itself — it already defines `Customer` / `Order` etc.
        // in other test files, and TPA includes every loaded assembly, which causes
        // CS0433 ("type exists in both") when the library redefines those names.
        var tpaReferences = TrustedReferences.Where(_ =>
            !_.EndsWith("StrongIdAnalyzer.Tests.dll", StringComparison.OrdinalIgnoreCase));

        var libraryTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(library) };
        if (index is not null)
        {
            // Separate tree so [assembly: ...] can legally appear after `using` but before
            // namespace declarations — smashing it into one file with the library types
            // would violate CS1730 ordering.
            libraryTrees.Add(CSharpSyntaxTree.ParseText(
                $"""
                [assembly: StrongIdAnalyzer.StrongIdIndexAttribute("{index}")]

                {indexAttributeDeclaration}
                """));
        }

        var libraryCompilation = CSharpCompilation.Create(
            "TestLib",
            libraryTrees,
            tpaReferences,
            new(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emit = libraryCompilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Library compilation failed: " +
                string.Join(Environment.NewLine, emit.Diagnostics));
        }

        var libraryReference = MetadataReference.CreateFromImage(stream.ToArray());

        var consumerCompilation = CSharpCompilation.Create(
            "TestConsumer",
            [CSharpSyntaxTree.ParseText(consumer)],
            [.. tpaReferences, libraryReference],
            new(OutputKind.DynamicallyLinkedLibrary));

        // Surface binding errors in the consumer — an unresolved reference to a
        // library symbol would silently give us `Unknown` source info and no diagnostic,
        // which is exactly the false-pass shape these tests must guard against.
        var consumerErrors = consumerCompilation.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (consumerErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "Consumer compilation has errors: " +
                string.Join(Environment.NewLine, consumerErrors.Select(_ => _.ToString())));
        }

        return consumerCompilation
            .WithAnalyzers([new IdMismatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }
}
