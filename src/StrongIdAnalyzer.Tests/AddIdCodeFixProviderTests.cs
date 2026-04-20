[TestFixture]
public class AddIdCodeFixProviderTests
{
    const string idAttributeSource =
        """
        global using StrongIdAnalyzer;

        namespace StrongIdAnalyzer;

        using System;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            Inherited = false)]
        sealed class IdAttribute(string type) : Attribute;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            Inherited = false)]
        sealed class UnionIdAttribute(params string[] types) : Attribute;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            Inherited = false)]
        sealed class IdAttribute<T> : Attribute;

        [AttributeUsage(
            AttributeTargets.Property |
            AttributeTargets.Field |
            AttributeTargets.Parameter |
            AttributeTargets.ReturnValue,
            Inherited = false)]
        sealed class UnionIdAttribute<T1, T2> : Attribute;
        """;

    [Test]
    public async Task SIA002_AddsAttributeToSourceProperty()
    {
        var source =
            """
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
        var source =
            """
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
        var source =
            """
            public class Holder
            {
                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use(System.Guid input) => Consume(input);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id<Order>] System.Guid input");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetParameter()
    {
        var source =
            """
            public class Target
            {
                public static void Consume(System.Guid value) { }
            }

            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public void Use() => Target.Consume(OrderId);
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id<Order>] System.Guid value");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetProperty()
    {
        var source =
            """
            public class Target
            {
                public System.Guid Value { get; set; }
            }

            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public void Use(Target target) => target.Value = OrderId;
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id<Order>]");
        Contains(fixedSource, "public System.Guid Value { get; set; }");
    }

    [Test]
    public async Task SIA002_BinaryEquality_AddsAttributeToUntaggedSide()
    {
        var source =
            """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public System.Guid Other { get; set; }

                public bool Check() => OrderId == Other;
            }
            """;

        var fixedSource = await ApplyFix(source);

        Contains(fixedSource, "[Id<Order>]");
        Contains(fixedSource, "public System.Guid Other { get; set; }");
    }

    [Test]
    public async Task SIA002_CustomIdValue()
    {
        var source =
            """
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
        var source =
            """
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

    [Test]
    public async Task SIA006_RewritesSingletonUnionAsId()
    {
        var source =
            """
            public class Holder
            {
                [UnionId("Customer")]
                public System.Guid Value { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA006");

        // Customer is a real class in the test assembly (Samples.cs) and is therefore
        // visible from the fix site, so the codefix prefers the generic form.
        Contains(fixedSource, "[Id<Customer>]");
        IsTrue(!fixedSource.Contains("UnionId"),
            $"Expected UnionId to be replaced but got:\n{fixedSource}");
    }

    [Test]
    public async Task SIA001_ReplacesExplicitAttributeOnTargetParameter()
    {
        var source =
            """
            public class Target
            {
                public static void Consume([Id("Bid")] System.Guid value) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        // Disambiguate: there's also a source-side "Change attribute on property 'Id'"
        // fix now (see SIA001_OffersFixOnSourceSide_WhenTargetHasExplicitAttribute).
        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute on parameter 'value'");

        Contains(fixedSource, "[Id(\"TreasuryBid\")] System.Guid value");
        DoesNotContain(fixedSource, "[Id(\"Bid\")]");
    }

    [Test]
    public async Task SIA001_AddsAttributeToConventionallyNamedParameter()
    {
        var source =
            """
            public class Target
            {
                public static void Consume(System.Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Add");

        Contains(fixedSource, "[Id(\"TreasuryBid\")] System.Guid bidId");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedParameter()
    {
        var source =
            """
            public class Target
            {
                public static void Consume(System.Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        Contains(fixedSource, "System.Guid treasuryBidId");
        DoesNotContain(fixedSource, "bidId");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedProperty()
    {
        var source =
            """
            public class Target
            {
                public System.Guid BidId { get; set; }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use(Target target) => target.BidId = Id;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        Contains(fixedSource, "public System.Guid TreasuryBidId");
        Contains(fixedSource, "target.TreasuryBidId = Id");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedField()
    {
        var source =
            """
            public class Target
            {
                public System.Guid BidId;
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use(Target target) => target.BidId = Id;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        Contains(fixedSource, "public System.Guid TreasuryBidId;");
        Contains(fixedSource, "target.TreasuryBidId = Id");
    }

    [Test]
    public async Task SIA001_NoRenameWhenExplicitAttributePresent()
    {
        var source =
            """
            public class Target
            {
                public static void Consume([Id("Bid")] System.Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var (document, diagnostic) = await PrepareFixAsync(source, "SIA001");
        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);

        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);

        var built = actions.ToImmutable();
        // Both sides have explicit attributes, so only "Change attribute" fixes are
        // offered (no rename variants) — one per side.
        AreEqual(2, built.Length);
        IsTrue(built.All(_ => _.Title.StartsWith("Change attribute", StringComparison.Ordinal)));
    }

    [Test]
    public async Task SIA001_InheritedId_ReceiverTypeDrivesRename()
    {
        // bid.Id inherits Id from BaseEntity. The fix should propose renaming the
        // target parameter using the receiver static type (TreasuryBid), not the
        // declaring type (BaseEntity).
        var source =
            """
            public class BaseEntity
            {
                public System.Guid Id { get; set; }
            }

            public class TreasuryBid : BaseEntity;

            public class Target
            {
                public static void BuildTreasureMeasures(System.Guid orderId) { }
            }

            public class Holder
            {
                public void Use(TreasuryBid bid) => Target.BuildTreasureMeasures(bid.Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        Contains(fixedSource, "System.Guid treasuryBidId");
        DoesNotContain(fixedSource, "orderId");
    }

    [Test]
    public async Task SIA001_InheritedId_ReceiverTypeDrivesChangeAttribute()
    {
        var source =
            """
            public class BaseEntity
            {
                public System.Guid Id { get; set; }
            }

            public class TreasuryBid : BaseEntity;

            public class Target
            {
                public static void BuildTreasureMeasures([Id("Other")] System.Guid value) { }
            }

            public class Holder
            {
                public void Use(TreasuryBid bid) => Target.BuildTreasureMeasures(bid.Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute");

        Contains(fixedSource, "[Id<TreasuryBid>] System.Guid value");
    }

    [Test]
    public async Task SIA001_OffersFixOnSourceSide_WhenTargetHasExplicitAttribute()
    {
        // Convention-tagged source + explicitly-tagged target: the fix must offer a
        // source-side option (override the convention with an explicit attribute on
        // the source declaration), not only the target-side option that would
        // demote the target's tag.
        var source =
            """
            public record ProgramBillOutcomeInput(System.Guid VariationId);

            public class Input
            {
                public System.Collections.Generic.IReadOnlyList<ProgramBillOutcomeInput> Outcomes { get; set; } = [];

                public ProgramBillOutcomeInput ForId([Id("VariationBase")] System.Guid guid) =>
                    System.Linq.Enumerable.Single(Outcomes, _ => _.VariationId == guid);
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        IsTrue(
            titles.Any(_ => _ == "Add [Id(\"VariationBase\")] to parameter 'VariationId'"),
            $"missing source-side add title, got: {string.Join(" | ", titles)}");
    }

    [Test]
    public async Task SIA001_AppliesFixOnSourceSide_Parameter()
    {
        var source =
            """
            public record ProgramBillOutcomeInput(System.Guid VariationId);

            public class Input
            {
                public System.Collections.Generic.IReadOnlyList<ProgramBillOutcomeInput> Outcomes { get; set; } = [];

                public ProgramBillOutcomeInput ForId([Id("VariationBase")] System.Guid guid) =>
                    System.Linq.Enumerable.Single(Outcomes, _ => _.VariationId == guid);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(
            source,
            "SIA001",
            "Add [Id(\"VariationBase\")] to parameter 'VariationId'");

        Contains(fixedSource, "[Id(\"VariationBase\")] System.Guid VariationId");
    }

    [Test]
    public async Task FixTitles_NameTheTargetHost()
    {
        // Titles should identify the kind and name of the declaration being acted on,
        // so the IDE popup tells the user which side of the call is affected.
        var mismatchSource =
            """
            public class Target
            {
                public static void Consume(System.Guid orderId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var mismatchTitles = (await GetCodeActions(mismatchSource)).Select(_ => _.Title).ToArray();
        // TreasuryBid is not a type in scope here (Bid is the only declared type), so the
        // string form is used.
        IsTrue(
            mismatchTitles.Any(_ => _ == "Add [Id(\"TreasuryBid\")] to parameter 'orderId'"),
            $"missing add title, got: {string.Join(" | ", mismatchTitles)}");
        IsTrue(
            mismatchTitles.Any(_ => _ == "Rename parameter 'orderId' to 'treasuryBidId'"),
            $"missing rename title, got: {string.Join(" | ", mismatchTitles)}");

        var changeSource =
            """
            public class Target
            {
                public static void Consume([Id("Order")] System.Guid value) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var changeTitles = (await GetCodeActions(changeSource)).Select(_ => _.Title).ToArray();
        IsTrue(
            changeTitles.Any(_ => _ == "Change attribute on parameter 'value' to [Id(\"TreasuryBid\")]"),
            $"missing change title, got: {string.Join(" | ", changeTitles)}");

        var redundantSource =
            """
            public class OrderRedundant
            {
                [Id("OrderRedundant")]
                public System.Guid Id { get; set; }
            }
            """;

        var redundantTitles = (await GetCodeActions(redundantSource)).Select(_ => _.Title).ToArray();
        IsTrue(
            redundantTitles.Any(_ => _ == "Remove redundant [Id] from property 'Id'"),
            $"missing redundant title, got: {string.Join(" | ", redundantTitles)}");

        var unionSource =
            """
            public class Holder
            {
                [UnionId("Order")]
                public System.Guid OrderId { get; set; }
            }
            """;

        var unionTitles = (await GetCodeActions(unionSource)).Select(_ => _.Title).ToArray();
        // Order is a real type in the test assembly so the codefix prefers the generic form.
        IsTrue(
            unionTitles.Any(_ => _ == "Replace [UnionId] on property 'OrderId' with [Id<Order>]"),
            $"missing union title, got: {string.Join(" | ", unionTitles)}");
    }

    [Test]
    public async Task SIA005_RemovesRedundantAttribute_WhenOnlyAttribute()
    {
        var source =
            """
            public class Order
            {
                [Id("Order")]
                public System.Guid Id { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA005");

        IsTrue(!fixedSource.Contains("[Id("),
            $"Expected attribute to be removed but got:\n{fixedSource}");
        Contains(fixedSource, "public System.Guid Id { get; set; }");
    }

    [Test]
    public async Task SIA005_RemovesRedundantAttribute_LeavingSiblings()
    {
        // When the redundant [Id] sits beside another attribute in the same list, only it
        // is removed.
        var source =
            """
            using System;

            public class Order
            {
                [Obsolete, Id("Order")]
                public System.Guid Id { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA005");

        IsTrue(!fixedSource.Contains("Id(\"Order\")"),
            $"Expected [Id(\"Order\")] to be removed but got:\n{fixedSource}");
        Contains(fixedSource, "Obsolete");
    }

    [Test]
    public async Task SIA003_UnionSource_OffersUnionAndPerValueFixes()
    {
        // Names avoid the Id/XxxId convention so the target genuinely reads as untagged.
        var source =
            """
            public class Target
            {
                public System.Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("Customer", "Order")]
                public System.Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();

        // Customer and Order are real types in the test assembly, so the codefix renders
        // both the union and per-value Id fixes in generic form.
        IsTrue(
            titles.Any(_ => _ == "Add [UnionId<Customer, Order>] to property 'Subject'"),
            $"missing union title, got: {string.Join(" | ", titles)}");
        IsTrue(
            titles.Any(_ => _ == "Add [Id<Customer>] to property 'Subject'"),
            $"missing Customer title, got: {string.Join(" | ", titles)}");
        IsTrue(
            titles.Any(_ => _ == "Add [Id<Order>] to property 'Subject'"),
            $"missing Order title, got: {string.Join(" | ", titles)}");
    }

    [Test]
    public void Provider_Exposes_AllFixableDiagnosticIds()
    {
        var ids = new AddIdCodeFixProvider().FixableDiagnosticIds.OrderBy(_ => _).ToArray();
        var expected = new[] { "SIA001", "SIA002", "SIA003", "SIA005", "SIA006" };
        AreEqual(expected.Length, ids.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            AreEqual(expected[i], ids[i]);
        }
    }

    [Test]
    public void Provider_FixAll_UsesBatchFixer() =>
        AreSame(WellKnownFixAllProviders.BatchFixer, new AddIdCodeFixProvider().GetFixAllProvider());

    [Test]
    public async Task SIA003_UnionSource_AppliesUnionFix_GenericForm()
    {
        // Companion to SIA003_UnionSource_OffersUnionAndPerValueFixes, which only
        // verifies the titles are registered. This actually applies the union fix and
        // checks the resulting [UnionId<...>] attribute is emitted in generic form.
        var source =
            """
            public class Customer;
            public class Order;

            public class Target
            {
                public System.Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("Customer", "Order")]
                public System.Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add [UnionId<Customer, Order>]");

        Contains(fixedSource, "[UnionId<Customer, Order>]");
    }

    [Test]
    public async Task SIA003_UnionSource_AppliesUnionFix_StringForm()
    {
        // When one of the union values isn't a valid C# identifier the codefix should
        // fall back to the string-arg form `[UnionId("a", "b")]` rather than generics.
        var source =
            """
            public class Order;

            public class Target
            {
                public System.Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("custom-tag", "Order")]
                public System.Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add [UnionId(");

        Contains(fixedSource, "[UnionId(\"custom-tag\", \"Order\")]");
        DoesNotContain(fixedSource, "[UnionId<");
    }

    [Test]
    public async Task SIA001_GenericExistingAttribute_PreservesGenericForm()
    {
        // When the target already uses [Id<Bid>], the fix to change the attribute should
        // produce [Id<TreasuryBid>] not [Id("TreasuryBid")].
        var source =
            """
            public class TreasuryBid;
            public class Bid;

            public class Target
            {
                public static void Consume([Id<Bid>] System.Guid value) { }
            }

            public class Holder
            {
                [Id<TreasuryBid>]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute on parameter 'value'");

        Contains(fixedSource, "[Id<TreasuryBid>] System.Guid value");
        DoesNotContain(fixedSource, "[Id(\"TreasuryBid\")]");
    }

    [Test]
    public async Task SIA002_PrefersGenericForm_WhenTagMatchesVisibleType()
    {
        // The tag "Election" is inferred from convention on Election.Id. The codefix
        // should suggest [Id<Election>] (not [Id("Election")]) because the Election
        // type is visible at the fix site.
        var source =
            """
            public class Election
            {
                public System.Guid Id { get; set; }

                public static System.Guid Election2022 = System.Guid.NewGuid();
            }

            public class Holder
            {
                public void Use(Election e) => e.Id = Election.Election2022;
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA002");

        Contains(fixedSource, "[Id<Election>]");
        DoesNotContain(fixedSource, "[Id(\"Election\")]");
    }

    [Test]
    public async Task SIA002_FallsBackToStringForm_WhenTagDoesNotMatchVisibleType()
    {
        var source =
            """
            public class Holder
            {
                public System.Guid Value { get; set; }

                public static void Consume([Id("NotATypeInScope")] System.Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA002");

        Contains(fixedSource, "[Id(\"NotATypeInScope\")]");
        DoesNotContain(fixedSource, "[Id<NotATypeInScope>]");
    }

    [Test]
    public async Task SIA001_GenericSourceAttribute_RenderedTitleUsesGenericForm()
    {
        // Both sides explicit, both generic: change-attribute titles on either side
        // should render in generic form.
        var source =
            """
            public class TreasuryBid;
            public class Bid;

            public class Target
            {
                public static void Consume([Id<Bid>] System.Guid value) { }
            }

            public class Holder
            {
                [Id<TreasuryBid>]
                public System.Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        IsTrue(
            titles.Any(_ => _ == "Change attribute on parameter 'value' to [Id<TreasuryBid>]"),
            $"missing generic target title, got: {string.Join(" | ", titles)}");
        IsTrue(
            titles.Any(_ => _ == "Change attribute on property 'Id' to [Id<Bid>]"),
            $"missing generic source title, got: {string.Join(" | ", titles)}");
    }

    static void Contains(string actual, string expected) =>
        IsTrue(
            actual.Contains(expected),
            $"Expected fixed source to contain:\n{expected}\n\nActual:\n{actual}");

    static void DoesNotContain(string actual, string unexpected) =>
        IsTrue(
            !actual.Contains(unexpected),
            $"Expected fixed source NOT to contain:\n{unexpected}\n\nActual:\n{actual}");

    static Task<string> ApplyFix(string source) =>
        ApplyFix(source, id: null);

    static async Task<string> ApplyFix(string source, string? id)
    {
        var (document, diagnostic) = await PrepareFixAsync(source, id);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);

        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);

        var action = actions.ToImmutable().Single();
        return await ApplyAction(action, document.Id);
    }

    static async Task<string> ApplyFixByTitlePrefix(string source, string id, string titlePrefix)
    {
        var (document, diagnostic) = await PrepareFixAsync(source, id);

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);

        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);

        var action = actions
            .ToImmutable()
            .Single(_ => _.Title.StartsWith(titlePrefix, StringComparison.Ordinal));
        return await ApplyAction(action, document.Id);
    }

    static async Task<string> ApplyAction(CodeAction action, DocumentId documentId)
    {
        var operations = await action.GetOperationsAsync(Cancel.None);
        var applyOperation = operations.OfType<ApplyChangesOperation>().Single();

        var newDocument = applyOperation.ChangedSolution.GetDocument(documentId)!;
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

    static Task<(Document Document, Diagnostic Diagnostic)> PrepareFixAsync(string source) =>
        PrepareFixAsync(source, id: null);

    static async Task<(Document Document, Diagnostic Diagnostic)> PrepareFixAsync(
        string source,
        string? id)
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "Tests",
            assemblyName: "Tests",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: TrustedReferences.All);

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

        var diagnostic = id is null
            ? diagnostics.Single()
            : diagnostics.Single(_ => _.Id == id);

        return (document, diagnostic);
    }
}
