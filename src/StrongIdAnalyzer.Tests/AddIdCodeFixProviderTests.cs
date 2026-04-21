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
            using System;

            public class Target
            {
                public void Consume([Id("Order")] Guid value) { }
            }

            public class Holder
            {
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id(\"Order\")]");
        await Contains(fixedSource, "public Guid Value { get; set; }");
    }

    [Test]
    public async Task SIA002_AddsAttributeToSourceField()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Field;

                public static void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(Field);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id(\"Order\")]");
        await Contains(fixedSource, "public Guid Field;");
    }

    [Test]
    public async Task SIA002_AddsAttributeToSourceParameter()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public static void Consume([Id("Order")] Guid value) { }

                public void Use(Guid input) => Consume(input);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id<Order>] Guid input");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetParameter()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume(Guid value) { }
            }

            public class Holder
            {
                public Guid OrderId { get; set; }

                public void Use() => Target.Consume(OrderId);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add");

        await Contains(fixedSource, "[Id<Order>] Guid value");
    }

    [Test]
    public async Task SIA003_AddsAttributeToTargetProperty()
    {
        var source =
            """
            using System;

            public class Target
            {
                public Guid Value { get; set; }
            }

            public class Holder
            {
                public Guid OrderId { get; set; }

                public void Use(Target target) => target.Value = OrderId;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add");

        await Contains(fixedSource, "[Id<Order>]");
        await Contains(fixedSource, "public Guid Value { get; set; }");
    }

    [Test]
    public async Task SIA002_BinaryEquality_AddsAttributeToUntaggedSide()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId { get; set; }

                public Guid Other { get; set; }

                public bool Check() => OrderId == Other;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id<Order>]");
        await Contains(fixedSource, "public Guid Other { get; set; }");
    }

    [Test]
    public async Task SIA002_CustomIdValue()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Value { get; set; }

                public static void Consume([Id("custom-tag")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var fixedSource = await ApplyFix(source);

        await Contains(fixedSource, "[Id(\"custom-tag\")]");
    }

    [Test]
    public async Task SIA002_RenamesSourceParameterToConventionName()
    {
        var source =
            """
            using System;

            public class Customer
            {
                [Id("Customer")]
                public Guid Id { get; set; }
            }

            public static class Extensions
            {
                public static bool Exists(Customer customer, Guid id) => customer.Id == id;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Rename");

        await Contains(fixedSource, "Guid customerId");
        await DoesNotContain(fixedSource, "Guid id)");
    }

    [Test]
    public async Task SIA003_RenamesTargetParameterToConventionName()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume(Guid value) { }
            }

            public class Holder
            {
                public Guid OrderId { get; set; }

                public void Use() => Target.Consume(OrderId);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Rename");

        await Contains(fixedSource, "Guid orderId");
        await DoesNotContain(fixedSource, "Guid value");
    }

    [Test]
    public async Task SIA002_NoRenameWhenTagNotValidIdentifier()
    {
        // custom-tag can't form a legal identifier, so only the attribute fix is offered.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Value { get; set; }

                public static void Consume([Id("custom-tag")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();

        await Assert.That(titles.Any(_ => _.StartsWith("Rename", StringComparison.Ordinal))).IsFalse();
        await Assert.That(titles.Any(_ => _.StartsWith("Add", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task SIA002_NoRenameForUnionIdSource()
    {
        // The source needs a UnionId to match; convention produces exactly one tag,
        // so no rename alternative is offered.
        var source =
            """
            using System;

            public class Customer;
            public class Order;

            public class Target
            {
                public Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("Customer", "Order")]
                public Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();

        await Assert.That(titles.Any(_ => _.StartsWith("Rename", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MultiDeclaratorField_NoFixRegistered()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid a, b;

                public static void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(a);
            }
            """;

        var actions = await GetCodeActions(source);

        await Assert.That(actions.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SIA006_RewritesSingletonUnionAsId()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer")]
                public Guid Value { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA006");

        // Customer is a real class in the test assembly (Samples.cs) and is therefore
        // visible from the fix site, so the codefix prefers the generic form.
        await Contains(fixedSource, "[Id<Customer>]");
        await Assert.That(!fixedSource.Contains("UnionId")).IsTrue();
    }

    [Test]
    public async Task SIA001_ReplacesExplicitAttributeOnTargetParameter()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume([Id("Bid")] Guid value) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        // Disambiguate: there's also a source-side "Change attribute on property 'Id'"
        // fix now (see SIA001_OffersFixOnSourceSide_WhenTargetHasExplicitAttribute).
        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute on parameter 'value'");

        await Contains(fixedSource, "[Id(\"TreasuryBid\")] Guid value");
        await DoesNotContain(fixedSource, "[Id(\"Bid\")]");
    }

    [Test]
    public async Task SIA001_AddsAttributeToConventionallyNamedParameter()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume(Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Add");

        await Contains(fixedSource, "[Id(\"TreasuryBid\")] Guid bidId");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedParameter()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume(Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        await Contains(fixedSource, "Guid treasuryBidId");
        await DoesNotContain(fixedSource, "bidId");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedProperty()
    {
        var source =
            """
            using System;

            public class Target
            {
                public Guid BidId { get; set; }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use(Target target) => target.BidId = Id;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        await Contains(fixedSource, "public Guid TreasuryBidId");
        await Contains(fixedSource, "target.TreasuryBidId = Id");
    }

    [Test]
    public async Task SIA001_RenamesConventionallyNamedField()
    {
        var source =
            """
            using System;

            public class Target
            {
                public Guid BidId;
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use(Target target) => target.BidId = Id;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        await Contains(fixedSource, "public Guid TreasuryBidId;");
        await Contains(fixedSource, "target.TreasuryBidId = Id");
    }

    [Test]
    public async Task SIA003_RenamesRecordPrimaryCtorIdParameter()
    {
        var source =
            """
            using System;

            public class CommitmentReportSummaryView
            {
                public Guid CommitmentId { get; init; }
            }

            public class Holder
            {
                public IdAndFirstPublished Make(CommitmentReportSummaryView v) => new(v.CommitmentId, null);
            }

            public record IdAndFirstPublished(Guid Id, int? FirstPublished);
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        await Assert.That(titles).Contains("Rename parameter 'Id' to 'CommitmentId'");

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Rename parameter 'Id'");
        await Contains(fixedSource, "public record IdAndFirstPublished(Guid CommitmentId, int? FirstPublished);");
        await Contains(fixedSource, "new(v.CommitmentId, null)");
    }

    [Test]
    public async Task SIA001_NoRenameWhenExplicitAttributePresent()
    {
        var source =
            """
            using System;

            public class Target
            {
                public static void Consume([Id("Bid")] Guid bidId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

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
        await Assert.That(built.Length).IsEqualTo(2);
        await Assert.That(built.All(_ => _.Title.StartsWith("Change attribute", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task SIA001_InheritedId_ReceiverTypeDrivesRename()
    {
        // bid.Id inherits Id from BaseEntity. The fix should propose renaming the
        // target parameter using the receiver static type (TreasuryBid), not the
        // declaring type (BaseEntity).
        var source =
            """
            using System;

            public class BaseEntity
            {
                public Guid Id { get; set; }
            }

            public class TreasuryBid : BaseEntity;

            public class Target
            {
                public static void BuildTreasureMeasures(Guid orderId) { }
            }

            public class Holder
            {
                public void Use(TreasuryBid bid) => Target.BuildTreasureMeasures(bid.Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Rename");

        await Contains(fixedSource, "Guid treasuryBidId");
        await DoesNotContain(fixedSource, "orderId");
    }

    [Test]
    public async Task SIA001_InheritedId_ReceiverTypeDrivesChangeAttribute()
    {
        var source =
            """
            using System;

            public class BaseEntity
            {
                public Guid Id { get; set; }
            }

            public class TreasuryBid : BaseEntity;

            public class Target
            {
                public static void BuildTreasureMeasures([Id("Other")] Guid value) { }
            }

            public class Holder
            {
                public void Use(TreasuryBid bid) => Target.BuildTreasureMeasures(bid.Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute");

        await Contains(fixedSource, "[Id<TreasuryBid>] Guid value");
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
            using System;

            public record ProgramBillOutcomeInput(Guid VariationId);

            public class Input
            {
                public System.Collections.Generic.IReadOnlyList<ProgramBillOutcomeInput> Outcomes { get; set; } = [];

                public ProgramBillOutcomeInput ForId([Id("VariationBase")] Guid guid) =>
                    System.Linq.Enumerable.Single(Outcomes, _ => _.VariationId == guid);
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        await Assert.That(titles.Any(_ => _ == "Add [Id(\"VariationBase\")] to parameter 'VariationId'")).IsTrue();
    }

    [Test]
    public async Task SIA001_AppliesFixOnSourceSide_Parameter()
    {
        var source =
            """
            using System;

            public record ProgramBillOutcomeInput(Guid VariationId);

            public class Input
            {
                public System.Collections.Generic.IReadOnlyList<ProgramBillOutcomeInput> Outcomes { get; set; } = [];

                public ProgramBillOutcomeInput ForId([Id("VariationBase")] Guid guid) =>
                    System.Linq.Enumerable.Single(Outcomes, _ => _.VariationId == guid);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(
            source,
            "SIA001",
            "Add [Id(\"VariationBase\")] to parameter 'VariationId'");

        await Contains(fixedSource, "[Id(\"VariationBase\")] Guid VariationId");
    }

    [Test]
    public async Task FixTitles_NameTheTargetHost()
    {
        // Titles should identify the kind and name of the declaration being acted on,
        // so the IDE popup tells the user which side of the call is affected.
        var mismatchSource =
            """
            using System;

            public class Target
            {
                public static void Consume(Guid orderId) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var mismatchTitles = (await GetCodeActions(mismatchSource)).Select(_ => _.Title).ToArray();
        // TreasuryBid is not a type in scope here (Bid is the only declared type), so the
        // string form is used.
        await Assert.That(mismatchTitles.Any(_ => _ == "Add [Id(\"TreasuryBid\")] to parameter 'orderId'")).IsTrue();
        await Assert.That(mismatchTitles.Any(_ => _ == "Rename parameter 'orderId' to 'treasuryBidId'")).IsTrue();

        var changeSource =
            """
            using System;

            public class Target
            {
                public static void Consume([Id("Order")] Guid value) { }
            }

            public class Bid
            {
                [Id("TreasuryBid")]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var changeTitles = (await GetCodeActions(changeSource)).Select(_ => _.Title).ToArray();
        await Assert.That(changeTitles.Any(_ => _ == "Change attribute on parameter 'value' to [Id(\"TreasuryBid\")]")).IsTrue();

        var redundantSource =
            """
            using System;

            public class OrderRedundant
            {
                [Id("OrderRedundant")]
                public Guid Id { get; set; }
            }
            """;

        var redundantTitles = (await GetCodeActions(redundantSource)).Select(_ => _.Title).ToArray();
        await Assert.That(redundantTitles.Any(_ => _ == "Remove redundant [Id] from property 'Id'")).IsTrue();

        var unionSource =
            """
            using System;

            public class Holder
            {
                [UnionId("Order")]
                public Guid OrderId { get; set; }
            }
            """;

        var unionTitles = (await GetCodeActions(unionSource)).Select(_ => _.Title).ToArray();
        // Order is a real type in the test assembly so the codefix prefers the generic form.
        await Assert.That(unionTitles.Any(_ => _ == "Replace [UnionId] on property 'OrderId' with [Id<Order>]")).IsTrue();
    }

    [Test]
    public async Task SIA005_RemovesRedundantAttribute_WhenOnlyAttribute()
    {
        var source =
            """
            using System;

            public class Order
            {
                [Id("Order")]
                public Guid Id { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA005");

        await Assert.That(!fixedSource.Contains("[Id(")).IsTrue();
        await Contains(fixedSource, "public Guid Id { get; set; }");
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
                public Guid Id { get; set; }
            }
            """;

        var fixedSource = await ApplyFix(source, "SIA005");

        await Assert.That(!fixedSource.Contains("Id(\"Order\")")).IsTrue();
        await Contains(fixedSource, "Obsolete");
    }

    [Test]
    public async Task SIA003_UnionSource_OffersUnionAndPerValueFixes()
    {
        // Names avoid the Id/XxxId convention so the target genuinely reads as untagged.
        var source =
            """
            using System;

            public class Target
            {
                public Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("Customer", "Order")]
                public Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();

        // Customer and Order are real types in the test assembly, so the codefix renders
        // both the union and per-value Id fixes in generic form.
        await Assert.That(titles.Any(_ => _ == "Add [UnionId<Customer, Order>] to property 'Subject'")).IsTrue();
        await Assert.That(titles.Any(_ => _ == "Add [Id<Customer>] to property 'Subject'")).IsTrue();
        await Assert.That(titles.Any(_ => _ == "Add [Id<Order>] to property 'Subject'")).IsTrue();
    }

    [Test]
    public async Task Provider_Exposes_AllFixableDiagnosticIds()
    {
        var ids = new AddIdCodeFixProvider().FixableDiagnosticIds.OrderBy(_ => _).ToArray();
        var expected = new[] { "SIA001", "SIA002", "SIA003", "SIA005", "SIA006" };
        await Assert.That(ids.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(ids[i]).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task Provider_FixAll_UsesBatchFixer() =>
        await Assert.That(new AddIdCodeFixProvider().GetFixAllProvider()).IsSameReferenceAs(WellKnownFixAllProviders.BatchFixer);

    [Test]
    public async Task SIA003_UnionSource_AppliesUnionFix_GenericForm()
    {
        // Companion to SIA003_UnionSource_OffersUnionAndPerValueFixes, which only
        // verifies the titles are registered. This actually applies the union fix and
        // checks the resulting [UnionId<...>] attribute is emitted in generic form.
        var source =
            """
            using System;

            public class Customer;
            public class Order;

            public class Target
            {
                public Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("Customer", "Order")]
                public Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add [UnionId<Customer, Order>]");

        await Contains(fixedSource, "[UnionId<Customer, Order>]");
    }

    [Test]
    public async Task SIA003_UnionSource_AppliesUnionFix_StringForm()
    {
        // When one of the union values isn't a valid C# identifier the codefix should
        // fall back to the string-arg form `[UnionId("a", "b")]` rather than generics.
        var source =
            """
            using System;

            public class Order;

            public class Target
            {
                public Guid Subject { get; set; }
            }

            public class Holder
            {
                [UnionId("custom-tag", "Order")]
                public Guid Subject { get; set; }

                public Target Create() => new Target { Subject = Subject };
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA003", "Add [UnionId(");

        await Contains(fixedSource, "[UnionId(\"custom-tag\", \"Order\")]");
        await DoesNotContain(fixedSource, "[UnionId<");
    }

    [Test]
    public async Task SIA001_GenericExistingAttribute_PreservesGenericForm()
    {
        // When the target already uses [Id<Bid>], the fix to change the attribute should
        // produce [Id<TreasuryBid>] not [Id("TreasuryBid")].
        var source =
            """
            using System;

            public class TreasuryBid;
            public class Bid;

            public class Target
            {
                public static void Consume([Id<Bid>] Guid value) { }
            }

            public class Holder
            {
                [Id<TreasuryBid>]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA001", "Change attribute on parameter 'value'");

        await Contains(fixedSource, "[Id<TreasuryBid>] Guid value");
        await DoesNotContain(fixedSource, "[Id(\"TreasuryBid\")]");
    }

    [Test]
    public async Task SIA002_PrefersGenericForm_WhenTagMatchesVisibleType()
    {
        // The tag "Election" is inferred from convention on Election.Id. The codefix
        // should suggest [Id<Election>] (not [Id("Election")]) because the Election
        // type is visible at the fix site.
        var source =
            """
            using System;

            public class Election
            {
                public Guid Id { get; set; }

                public static Guid Election2022 = Guid.NewGuid();
            }

            public class Holder
            {
                public void Use(Election e) => e.Id = Election.Election2022;
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id<Election>]");
        await DoesNotContain(fixedSource, "[Id(\"Election\")]");
    }

    [Test]
    public async Task SIA002_OnlyExplicitTags_WhenTaggedSideHasExplicitAttribute()
    {
        // ModifiedById has explicit [Id<User>] on the implementation, plus inherits
        // an interface member named "ModifiedById" whose convention tag is "ModifiedBy".
        // The fix should propose [Id<User>] only — convention-derived tags are
        // inferences, not declarations, so suggesting them here would override the
        // deliberate annotation already on BaseEntity.ModifiedById.
        var source =
            """
            using System;

            public class User { public Guid Id { get; set; } }

            public interface IModified
            {
                Guid ModifiedById { get; set; }
            }

            public abstract class BaseEntity : IModified
            {
                [Id<User>]
                public Guid ModifiedById { get; set; }
            }

            public class PoliticalParty : BaseEntity { }

            public class Seeder
            {
                public static Guid System = Guid.NewGuid();

                public PoliticalParty Make() => new() { ModifiedById = System };
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        // Two fixes: add the attribute, or rename the field to the convention form.
        // The convention-derived tag "ModifiedBy" from the interface member is NOT
        // offered — it's an inference, not a declaration, and suggesting it would
        // override the deliberate [Id<User>] already on BaseEntity.ModifiedById.
        await Assert.That(titles.Length).IsEqualTo(2);
        await Assert.That(titles).Contains("Add [Id<User>] to field 'System'");
        await Assert.That(titles).Contains("Rename field 'System' to 'UserId'");
    }

    [Test]
    public async Task SIA002_PrefersGenericForm_WhenDiagnosticTreeIsStale()
    {
        // Simulates an out-of-process analyzer host (Rider): the diagnostic's
        // Location.SourceTree is re-parsed on the fix side, so its identity no
        // longer matches any tree in context.Document.Project.Solution. The
        // codefix must still resolve the host to the live tree by file path
        // and emit the generic form.
        var source =
            """
            using System;

            public class Election
            {
                public Guid Id { get; set; }

                public static Guid Election2022 = Guid.NewGuid();
            }

            public class Holder
            {
                public void Use(Election e) => e.Id = Election.Election2022;
            }
            """;

        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "Tests",
            assemblyName: "Tests",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: TrustedReferences.All);
        var idAttrId = DocumentId.CreateNewId(projectInfo.Id);
        var documentId = DocumentId.CreateNewId(projectInfo.Id);
        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(idAttrId, "IdAttribute.cs", idAttributeSource, filePath: "IdAttribute.cs")
            .AddDocument(documentId, "Test.cs", source, filePath: "Test.cs");

        var compilation = (await solution.GetProject(projectInfo.Id)!.GetCompilationAsync())!;
        var diagnostics = await compilation
            .WithAnalyzers([new IdMismatchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        var diagnostic = diagnostics.Single(_ => _.Id == "SIA002");

        // Fork: replace the document text with equivalent content. Roslyn produces
        // a new SyntaxTree identity; the diagnostic's location still points at the
        // old tree, which is no longer in the project's compilation.
        var refreshedSolution = solution.WithDocumentText(
            documentId,
            Microsoft.CodeAnalysis.Text.SourceText.From(source));
        var refreshedDocument = refreshedSolution.GetDocument(documentId)!;

        var staleTree = diagnostic.AdditionalLocations[0].SourceTree!;
        var refreshedCompilation = (await refreshedDocument.Project.GetCompilationAsync())!;
        await Assert.That(refreshedCompilation.SyntaxTrees.Contains(staleTree)).IsFalse();
        await Assert.That(staleTree.FilePath).IsEqualTo("Test.cs");

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            refreshedDocument,
            diagnostic,
            (action, _) => actions.Add(action),
            Cancel.None);
        await new AddIdCodeFixProvider().RegisterCodeFixesAsync(context);

        var titles = actions.Select(_ => _.Title).ToArray();
        await Assert.That(titles.Any(_ => _.Contains("[Id<Election>]"))).IsTrue();
        await Assert.That(titles.Any(_ => _.Contains("[Id(\"Election\")]"))).IsFalse();
    }

    [Test]
    public async Task SIA002_FallsBackToStringForm_WhenTagDoesNotMatchVisibleType()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Value { get; set; }

                public static void Consume([Id("NotATypeInScope")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var fixedSource = await ApplyFixByTitlePrefix(source, "SIA002", "Add");

        await Contains(fixedSource, "[Id(\"NotATypeInScope\")]");
        await DoesNotContain(fixedSource, "[Id<NotATypeInScope>]");
    }

    [Test]
    public async Task SIA001_GenericSourceAttribute_RenderedTitleUsesGenericForm()
    {
        // Both sides explicit, both generic: change-attribute titles on either side
        // should render in generic form.
        var source =
            """
            using System;

            public class TreasuryBid;
            public class Bid;

            public class Target
            {
                public static void Consume([Id<Bid>] Guid value) { }
            }

            public class Holder
            {
                [Id<TreasuryBid>]
                public Guid Id { get; set; }

                public void Use() => Target.Consume(Id);
            }
            """;

        var titles = (await GetCodeActions(source)).Select(_ => _.Title).ToArray();
        await Assert.That(titles.Any(_ => _ == "Change attribute on parameter 'value' to [Id<TreasuryBid>]")).IsTrue();
        await Assert.That(titles.Any(_ => _ == "Change attribute on property 'Id' to [Id<Bid>]")).IsTrue();
    }

    static async Task Contains(string actual, string expected) =>
        await Assert.That(actual).Contains(expected);

    static async Task DoesNotContain(string actual, string unexpected) =>
        await Assert.That(actual).DoesNotContain(unexpected);

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
