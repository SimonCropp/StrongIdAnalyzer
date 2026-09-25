// Exact-text assertions for every diagnostic message. The message is the only part of a
// diagnostic that survives into build output, so its wording is a contract: it has to
// name both declarations, spell out the attribute to write, and say where the fix
// lands. A change here is a change to what CI logs and AI agents get to read.
public class MessageTests
{
    // No `Tenant` type exists, so `[Id<Tenant>]` would not compile: the Fix clause falls
    // back to the string form rather than promising an attribute that breaks the build.
    [Test]
    public async Task SIA002_TagWithoutType_SuggestsStringForm()
    {
        var source =
            """
            using System;
            public class Sample
            {
                public Guid Raw { get; set; }
                public static void Place([Id("Tenant")] Guid tenantKey) { }
                public void Run() => Place(Raw);
            }
            """;

        var diagnostic = await Single(source, "SIA002");

        await Assert.That(diagnostic.GetMessage()).Contains("""Fix: add [Id("Tenant")] to property 'Sample.Raw'""");
    }

    [Test]
    public async Task SIA001_Argument()
    {
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class OrderService
            {
                public static void Place(Guid orderId) { }
                public void Run(Customer customer) => Place(customer.Id);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage())
            .IsEqualTo(
            "property 'Customer.Id' is [Id<Customer>] and flows to parameter 'orderId' of 'OrderService.Place', which is [Id<Order>]. Fix: apply [Id<Customer>] to parameter 'orderId' of 'OrderService.Place' (line 5), or pass a value tagged [Id<Order>].");
    }

    [Test]
    public async Task SIA001_Equality()
    {
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class Checks
            {
                public static bool Same(Customer customer, Guid orderId) => customer.Id == orderId;
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage())
            .IsEqualTo(
            "property 'Customer.Id' is [Id<Customer>] and is compared with parameter 'orderId' of 'Checks.Same', which is [Id<Order>]. Fix: apply [Id<Customer>] to parameter 'orderId' of 'Checks.Same' (line 5), or pass a value tagged [Id<Order>].");
    }

    [Test]
    public async Task SIA001_NeitherSideEditable_OnlySuggestsValue()
    {
        // Wrapper-derived tags have no declaration to edit, so the fix clause can only
        // ask for a different value.
        var source =
            """
            using System;
            public readonly record struct CustomerId(Guid Value);
            public readonly record struct OrderId(Guid Value);
            public class Service
            {
                public static void Place(OrderId orderId) { }
                public static void Run(CustomerId customerId) => Place(new OrderId(customerId.Value));
            }
            """;

        var diagnostics = await Run(("Service.cs", source), wrappers: true);
        var sia001 = diagnostics.Where(_ => _.Id == "SIA001").ToArray();

        await Assert.That(sia001.Length).IsEqualTo(1);
        await Assert.That(sia001[0].GetMessage()).IsEqualTo(
            "property 'CustomerId.Value' is [Id<Customer>] and flows to parameter 'Value' of 'OrderId.OrderId', which is [Id<Order>]. Fix: pass a value tagged [Id<Order>].");
    }

    [Test]
    public async Task SIA001_InheritedSource_FixUsesReceiverTag()
    {
        // leaf.Id carries every type between the receiver and the declaring type. The
        // body shows that whole set; the Fix clause names only the first (most-derived)
        // tag because that is the single value the fixer applies to the target.
        var source =
            """
            using System;
            public class Root { public Guid Id { get; set; } }
            public class Mid : Root { }
            public class Leaf : Mid { }
            public class Service
            {
                public static void TakeOther(Guid otherId) { }
                public void Run(Leaf leaf) => TakeOther(leaf.Id);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """property 'Root.Id' is [UnionId<Leaf, Mid, Root>] and flows to parameter 'otherId' of 'Service.TakeOther', which is [Id("Other")]. Fix: apply [Id<Leaf>] to parameter 'otherId' of 'Service.TakeOther' (line 7), or pass a value tagged [Id("Other")].""");
    }

    [Test]
    public async Task SIA001_UnionSource_FixUsesFirstOption()
    {
        var source =
            """
            using System;
            public class Service
            {
                public static void Place(Guid orderId) { }
                public void Run([UnionId("Customer", "Product")] Guid key) => Place(key);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "parameter 'key' of 'Service.Run' is [UnionId<Customer, Product>] and flows to parameter 'orderId' of 'Service.Place', which is [Id<Order>]. Fix: apply [Id<Customer>] to parameter 'orderId' of 'Service.Place' (line 4), or pass a value tagged [Id<Order>].");
    }

    [Test]
    public async Task SIA001_UnionTarget_ValueClauseShowsWholeSet()
    {
        // "pass a value tagged" keeps the target's full set: any one of them satisfies it.
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class Service
            {
                public static void Lookup([UnionId("Order", "Product")] Guid key) { }
                public void Run(Customer customer) => Lookup(customer.Id);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "property 'Customer.Id' is [Id<Customer>] and flows to parameter 'key' of 'Service.Lookup', which is [UnionId<Order, Product>]. Fix: apply [Id<Customer>] to parameter 'key' of 'Service.Lookup' (line 5), or pass a value tagged [UnionId<Order, Product>].");
    }

    // With suffix inference off, a qualifier word in the name ("template") becomes part of
    // the tag. The option exists for exactly this shape, so the message names it.
    [Test]
    public async Task SIA001_SuffixInferenceWouldResolve_HintsTheOption()
    {
        var source =
            """
            public class ExternalObject { public int Id { get; set; } }
            public class Service
            {
                public static void Get(int externalObjectId) { }
                public void Copy(int templateExternalObjectId) => Get(templateExternalObjectId);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """parameter 'templateExternalObjectId' of 'Service.Copy' is [Id("TemplateExternalObject")] and flows to parameter 'externalObjectId' of 'Service.Get', which is [Id<ExternalObject>]. Fix: apply [Id("TemplateExternalObject")] to parameter 'externalObjectId' of 'Service.Get' (line 4), or pass a value tagged [Id<ExternalObject>]. Alternatively, set strongidanalyzer.infer_suffix_ids = true in .editorconfig, which infers [Id<ExternalObject>] from 'templateExternalObjectId'.""");
    }

    // Neither side alone clears the mismatch; both re-read together do.
    [Test]
    public async Task SIA001_SuffixInferenceWouldResolve_BothSides()
    {
        var source =
            """
            using System;
            public class Product { public Guid Id { get; set; } }
            public class Service
            {
                public static void Copy(Guid targetProductId) { }
                public void Run(Guid sourceProductId) => Copy(sourceProductId);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """parameter 'sourceProductId' of 'Service.Run' is [Id("SourceProduct")] and flows to parameter 'targetProductId' of 'Service.Copy', which is [Id("TargetProduct")]. Fix: apply [Id("SourceProduct")] to parameter 'targetProductId' of 'Service.Copy' (line 5), or pass a value tagged [Id("TargetProduct")]. Alternatively, set strongidanalyzer.infer_suffix_ids = true in .editorconfig, which infers [Id<Product>] from 'sourceProductId' and [Id<Product>] from 'targetProductId'.""");
    }

    [Test]
    public async Task SIA001_SuffixInferenceWouldResolve_Equality()
    {
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class Checks
            {
                public static bool Same(Customer customer, Guid templateCustomerId) => customer.Id == templateCustomerId;
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """property 'Customer.Id' is [Id<Customer>] and is compared with parameter 'templateCustomerId' of 'Checks.Same', which is [Id("TemplateCustomer")]. Fix: apply [Id<Customer>] to parameter 'templateCustomerId' of 'Checks.Same' (line 5), or pass a value tagged [Id("TemplateCustomer")]. Alternatively, set strongidanalyzer.infer_suffix_ids = true in .editorconfig, which infers [Id<Customer>] from 'templateCustomerId'.""");
    }

    // The suffix rule would read `templateCustomerId` as "Customer", which still is not
    // "Order" — the hint would send the reader to an option that changes nothing.
    [Test]
    public async Task SIA001_SuffixInferenceWouldNotResolve_NoHint()
    {
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class Order { public Guid Id { get; set; } }
            public class Service
            {
                public static void Place(Guid orderId) { }
                public void Run(Guid templateCustomerId) => Place(templateCustomerId);
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """parameter 'templateCustomerId' of 'Service.Run' is [Id("TemplateCustomer")] and flows to parameter 'orderId' of 'Service.Place', which is [Id<Order>]. Fix: apply [Id("TemplateCustomer")] to parameter 'orderId' of 'Service.Place' (line 6), or pass a value tagged [Id<Order>].""");
    }

    // With the option on, a name it could not re-read is reported as before, and no
    // hint points at an option that is already set.
    [Test]
    public async Task SIA001_SuffixInferenceOn_NoHint()
    {
        var source =
            """
            using System;
            public class Customer { public Guid Id { get; set; } }
            public class Service
            {
                public static void Place(Guid orderId) { }
                public void Run(Guid templateCustomerId) => Place(templateCustomerId);
            }
            """;

        var diagnostics = await Run([("Sample.cs", source)], wrappers: false, suffix: true);
        var sia001 = diagnostics.Where(_ => _.Id == "SIA001").ToArray();

        await Assert.That(sia001.Length).IsEqualTo(1);
        await Assert.That(sia001[0].GetMessage()).DoesNotContain("infer_suffix_ids");
    }

    [Test]
    public async Task SIA002_SingleTag()
    {
        var source =
            """
            using System;
            public class Sample
            {
                public Guid Raw { get; set; }
                public static void Place(Guid orderId) { }
                public void Run() => Place(Raw);
            }
            """;

        var diagnostic = await Single(source, "SIA002");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "property 'Sample.Raw' has no [Id] but flows to parameter 'orderId' of 'Sample.Place', which is [Id<Order>]. Fix: add [Id<Order>] to property 'Sample.Raw' (line 4).");
    }

    [Test]
    public async Task SIA002_UnionTarget_SuggestsUnionId()
    {
        var source =
            """
            using System;
            public class Sample
            {
                public Guid Raw { get; set; }
                public static void Lookup([UnionId("Customer", "Product")] Guid key) { }
                public void Run() => Lookup(Raw);
            }
            """;

        var diagnostic = await Single(source, "SIA002");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "property 'Sample.Raw' has no [Id] but flows to parameter 'key' of 'Sample.Lookup', which is [UnionId<Customer, Product>]. Fix: add [UnionId<Customer, Product>] to property 'Sample.Raw' (line 4).");
    }

    [Test]
    public async Task SIA002_Equality()
    {
        var source =
            """
            using System;
            public class Sample
            {
                public Guid Raw { get; set; }
                public Guid OrderId { get; set; }
                public bool Same() => Raw == OrderId;
            }
            """;

        var diagnostic = await Single(source, "SIA002");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "property 'Sample.Raw' has no [Id] but is compared with property 'Sample.OrderId', which is [Id<Order>]. Fix: add [Id<Order>] to property 'Sample.Raw' (line 4).");
    }

    [Test]
    public async Task SIA003_SameFile()
    {
        var source =
            """
            using System;
            public class Sample
            {
                public Guid OrderId { get; set; }
                public static void Consume(Guid value) { }
                public void Run() => Consume(OrderId);
            }
            """;

        var diagnostic = await Single(source, "SIA003");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "property 'Sample.OrderId' is [Id<Order>] but flows to parameter 'value' of 'Sample.Consume', which has no [Id]. Fix: add [Id<Order>] to parameter 'value' of 'Sample.Consume' (line 5).");
    }

    [Test]
    public async Task SIA003_UnionSource_SuggestsUnionId()
    {
        var source =
            """
            using System;
            public class Service
            {
                public static void Consume(Guid value) { }
                public void Run([UnionId("Customer", "Product")] Guid key) => Consume(key);
            }
            """;

        var diagnostic = await Single(source, "SIA003");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            "parameter 'key' of 'Service.Run' is [UnionId<Customer, Product>] but flows to parameter 'value' of 'Service.Consume', which has no [Id]. Fix: add [UnionId<Customer, Product>] to parameter 'value' of 'Service.Consume' (line 4).");
    }

    [Test]
    public async Task SIA003_OtherFile_NamesThePath()
    {
        var sink =
            """
            using System;
            public static class Sink
            {
                public static void Consume(Guid value) { }
            }
            """;
        var caller =
            """
            using System;
            public class Sample
            {
                public Guid OrderId { get; set; }
                public void Run() => Sink.Consume(OrderId);
            }
            """;

        var diagnostics = await Run(("Sink.cs", sink), ("Sample.cs", caller));
        var sia003 = diagnostics.Where(_ => _.Id == "SIA003").ToArray();

        await Assert.That(sia003.Length).IsEqualTo(1);
        await Assert.That(sia003[0].GetMessage()).IsEqualTo(
            "property 'Sample.OrderId' is [Id<Order>] but flows to parameter 'value' of 'Sink.Consume', which has no [Id]. Fix: add [Id<Order>] to parameter 'value' of 'Sink.Consume' (Sink.cs:4).");
    }

    [Test]
    public async Task SIA004_NamesBothDeclarations()
    {
        var source =
            """
            using System;
            namespace Sales { public class Order { public Guid Id { get; set; } } }
            namespace Billing { public class Order { public Guid Id { get; set; } } }
            """;

        var diagnostics = await Run(("Orders.cs", source));
        var messages = diagnostics
            .Where(_ => _.Id == "SIA004")
            .Select(_ => _.GetMessage())
            .OrderBy(_ => _)
            .ToArray();

        await Assert.That(messages).IsEquivalentTo(
        [
            """property 'Billing.Order.Id' and property 'Sales.Order.Id' both infer the conventional Id name "Order" from their declaring type name. Fix: add an explicit [Id("...")] with a distinct value to at least one of them.""",
            """property 'Sales.Order.Id' and property 'Billing.Order.Id' both infer the conventional Id name "Order" from their declaring type name. Fix: add an explicit [Id("...")] with a distinct value to at least one of them."""
        ]);
    }

    [Test]
    public async Task SIA005()
    {
        var source =
            """
            using System;
            public class Customer
            {
                [Id("Customer")]
                public Guid Id { get; set; }
            }
            """;

        var diagnostic = await Single(source, "SIA005");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[Id("Customer")] on property 'Customer.Id' is redundant: the naming convention already infers "Customer". Fix: remove the attribute.""");
    }

    // The message quotes the attribute as written, since that is the text to delete.
    [Test]
    public async Task SIA005_GenericForm()
    {
        var source =
            """
            using System;
            public class Customer
            {
                [Id<Customer>]
                public Guid Id { get; set; }
            }
            """;

        var diagnostic = await Single(source, "SIA005");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[Id<Customer>] on property 'Customer.Id' is redundant: the naming convention already infers "Customer". Fix: remove the attribute.""");
    }

    // The name would infer "PerformedBy", so blaming the naming convention here would send the
    // reader looking in the wrong place. The parameter's own attribute is what makes the
    // property-targeted one redundant.
    [Test]
    public async Task SIA005_RecordPrimaryConstructorParameter()
    {
        var source =
            """
            using System;
            public record Change([Id("User")][property: Id("User")] Guid PerformedById);
            """;

        var diagnostic = await Single(source, "SIA005");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[Id("User")] on property 'Change.PerformedById' is redundant: the record primary-constructor parameter already carries "User". Fix: remove the attribute.""");
    }

    [Test]
    public async Task SIA005_Inherited()
    {
        var source =
            """
            using System;
            public interface IOwned
            {
                [Id("User")]
                Guid OwnerId { get; }
            }
            public class Thing : IOwned
            {
                [Id("User")]
                public Guid OwnerId { get; set; }
            }
            """;

        var diagnostic = await Single(source, "SIA005");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[Id("User")] on property 'Thing.OwnerId' is redundant: it already inherits "User" from the member it overrides or implements. Fix: remove the attribute.""");
    }

    [Test]
    public async Task SIA006()
    {
        var source =
            """
            using System;
            public class Sample
            {
                [UnionId("Customer")]
                public Guid Key { get; set; }
            }
            """;

        var diagnostic = await Single(source, "SIA006");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[UnionId("Customer")] on property 'Sample.Key' has only one option. Fix: replace it with [Id<Customer>].""");
    }

    [Test]
    public async Task SIA007()
    {
        var source =
            """
            using System;
            public class Sample
            {
                [Id("")]
                public Guid Key { get; set; }
            }
            """;

        var diagnostic = await Single(source, "SIA007");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[Id] on property 'Sample.Key' has an empty tag. Fix: supply a non-empty domain name, e.g. [Id("Customer")].""");
    }

    [Test]
    public async Task SIA008_MissingMember()
    {
        var source =
            """
            using System.Diagnostics;
            [assembly: ExternalId(typeof(Process), "Idd", "Process")]
            public class Sample { }
            """;

        var diagnostic = await Single(source, "SIA008");

        await Assert.That(diagnostic.GetMessage())
            .IsEqualTo(
                "[ExternalId] for 'Process.Idd' names no property or field of that type or its bases. Fix: reference an existing member, e.g. nameof(Process.Id).");
    }

    [Test]
    public async Task SIA008_EmptyId()
    {
        var source =
            """
            using System.Diagnostics;
            [assembly: ExternalId(typeof(Process), nameof(Process.Id))]
            public class Sample { }
            """;

        var diagnostic = await Single(source, "SIA008");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """[ExternalId] for 'Process.Id' has no id. Fix: supply a non-empty domain name, e.g. [assembly: ExternalId(typeof(Process), "Id", "Customer")].""");
    }

    [Test]
    public async Task SIA001_ExternalIdSource()
    {
        // The source is a framework member tagged through [assembly: ExternalId]; it has
        // no editable declaration, so the fix clause can only point at the target.
        var source =
            """
            using System.Diagnostics;
            [assembly: ExternalId(typeof(Process), nameof(Process.Id), "Process")]
            public class Sample
            {
                public int JobId { get; set; }
                public void Run(Process process) => JobId = process.Id;
            }
            """;

        var diagnostic = await Single(source, "SIA001");

        await Assert.That(diagnostic.GetMessage()).IsEqualTo(
            """property 'Process.Id' is [Id<Process>] and flows to property 'Sample.JobId', which is [Id("Job")]. Fix: apply [Id<Process>] to property 'Sample.JobId' (line 5), or pass a value tagged [Id("Job")].""");
    }

    [Test]
    public async Task EveryRule_HasHelpLinkAndDescription()
    {
        foreach (var descriptor in new IdMismatchAnalyzer().SupportedDiagnostics)
        {
            await Assert.That(descriptor.HelpLinkUri)
                .IsEqualTo($"https://github.com/SimonCropp/StrongIdAnalyzer/blob/main/docs/{descriptor.Id}.md");
            await Assert.That(descriptor.Description.ToString().Length).IsGreaterThan(0);
        }
    }

    static async Task<Diagnostic> Single(string source, string id)
    {
        var diagnostics = await Run(("Sample.cs", source));
        var matching = diagnostics.Where(_ => _.Id == id).ToArray();
        await Assert.That(matching.Length).IsEqualTo(1);
        return matching[0];
    }

    static Task<ImmutableArray<Diagnostic>> Run(params (string Path, string Source)[] files) =>
        Run(files, wrappers: false);

    static Task<ImmutableArray<Diagnostic>> Run((string Path, string Source) file, bool wrappers) =>
        Run([file], wrappers);

    static Task<ImmutableArray<Diagnostic>> Run((string Path, string Source)[] files, bool wrappers, bool suffix = false)
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

        var options = new Dictionary<string, string>();
        if (wrappers)
        {
            options["strongidanalyzer.infer_wrapper_ids"] = "true";
        }

        if (suffix)
        {
            options["strongidanalyzer.infer_suffix_ids"] = "true";
        }

        var analyzerOptions = new AnalyzerOptions([], new TestAnalyzerConfigOptionsProvider(options));
        return updated
            .SuppressStringTagHint()
            .WithAnalyzers([new IdMismatchAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }
}
