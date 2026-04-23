public class IdMismatchAnalyzerTests
{
    [Test]
    public async Task IdMismatch_ArgumentToParameter()
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
                [Id("Customer")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task IdMismatch_PropertyToProperty_Assignment()
    {
        // OrderId / CustomerId are auto-tagged by the naming convention.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId { get; set; }

                public Guid CustomerId { get; set; }

                public void Copy() => OrderId = CustomerId;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task IdMismatch_ObjectInitializer()
    {
        var source =
            """
            using System;

            public class Target
            {
                public Guid OrderId { get; set; }
            }

            public class Holder
            {
                public Guid CustomerId { get; set; }

                public Target Create() => new Target { OrderId = CustomerId };
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task GenericIdAttribute_Matching_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Customer;
            public class Target
            {
                public void Consume([Id<Customer>] Guid value) { }
            }
            public class Holder
            {
                [Id<Customer>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GenericIdAttribute_Mismatch_ReportsSIA001()
    {
        var source =
            """
            using System;

            public class Customer;
            public class Order;
            public class Target
            {
                public void Consume([Id<Order>] Guid value) { }
            }
            public class Holder
            {
                [Id<Customer>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task GenericIdAttribute_EquivalentToStringForm()
    {
        // Source uses the generic form, target uses the string form — they must resolve
        // to the same tag and not produce a mismatch.
        var source =
            """
            using System;

            public class Customer;
            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }
            public class Holder
            {
                [Id<Customer>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GenericUnionIdAttribute_SourceInOptions_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Customer;
            public class Order;
            public class Target
            {
                public void Consume([UnionId<Customer, Order>] Guid value) { }
            }
            public class Holder
            {
                [Id<Customer>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GenericUnionIdAttribute_SourceNotInOptions_ReportsSIA001()
    {
        var source =
            """
            using System;

            public class Customer;
            public class Order;
            public class Product;
            public class Target
            {
                public void Consume([UnionId<Customer, Order>] Guid value) { }
            }
            public class Holder
            {
                [Id<Product>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task GenericUnionIdAttribute_FiveArgs_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class A;
            public class B;
            public class C;
            public class D;
            public class E;
            public class Target
            {
                public void Consume([UnionId<A, B, C, D, E>] Guid value) { }
            }
            public class Holder
            {
                [Id<E>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GenericUnionIdAttribute_EquivalentToStringForm()
    {
        var source =
            """
            using System;

            public class Customer;
            public class Order;
            public class Target
            {
                public void Consume([UnionId("Customer", "Order")] Guid value) { }
            }
            public class Holder
            {
                [UnionId<Customer, Order>]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DerivedTagFlowsToBaseTarget_NoDiagnostic()
    {
        var source =
            """
            using System;

            public abstract class ProgramBillBase;
            public class ProgramBill : ProgramBillBase;
            public class Target
            {
                public void Consume([Id("ProgramBillBase")] Guid value) { }
            }
            public class Holder
            {
                [Id("ProgramBill")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BaseTagToDerivedTarget_ReportsSIA001()
    {
        // Opposite direction of covariance: a base-tagged value must NOT silently flow
        // into a derived-tagged target. Only the source side widens.
        var source =
            """
            using System;

            public abstract class ProgramBillBase;
            public class ProgramBill : ProgramBillBase;
            public class Target
            {
                public void Consume([Id("ProgramBill")] Guid value) { }
            }
            public class Holder
            {
                [Id("ProgramBillBase")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InterfaceTagFlowsFromImplementingTag_NoDiagnostic()
    {
        var source =
            """
            using System;

            public interface IBill;
            public class ProgramBill : IBill;
            public class Target
            {
                public void Consume([Id("IBill")] Guid value) { }
            }
            public class Holder
            {
                [Id("ProgramBill")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnresolvedTag_KeepsExactMatchSemantics()
    {
        // Tag names that don't correspond to any type in the compilation should behave
        // as before — no widening, exact match only.
        var source =
            """
            using System;

            public class Target
            {
                public void Consume([Id("LegacyThingA")] Guid value) { }
            }
            public class Holder
            {
                [Id("LegacyThingB")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task DeepInheritanceChain_GrandbaseTargetAcceptsDerived_NoDiagnostic()
    {
        // Confirms the base walk traverses more than one level — a three-deep chain still
        // widens the source tag all the way up to the grandbase.
        var source =
            """
            using System;

            public abstract class Root;
            public abstract class Middle : Root;
            public class Leaf : Middle;
            public class Target
            {
                public void Consume([Id("Root")] Guid value) { }
            }
            public class Holder
            {
                [Id("Leaf")]
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Equality_BaseAndDerivedTags_NoDiagnostic()
    {
        // Equality is symmetric — widening both sides lets a derived id compare to a
        // base id without firing SIA001.
        var source =
            """
            using System;

            public abstract class ProgramBillBase {}
            public class ProgramBill : ProgramBillBase {}
            public class Holder
            {
                [Id("ProgramBill")]
                public Guid Derived { get; set; }

                [Id("ProgramBillBase")]
                public Guid Base { get; set; }

                public bool Compare() => Derived == Base;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MethodReturnSource_Untagged_IsUnknown()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid GetValue() => Guid.Empty;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(GetValue());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MethodReturnSource_TaggedMatching_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [return: Id("Order")]
                public Guid GetOrderId() => Guid.Empty;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(GetOrderId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MethodReturnSource_TaggedMismatch_FiresSIA001()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [return: Id("Customer")]
                public Guid GetCustomerId() => Guid.Empty;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(GetCustomerId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task MethodReturnSource_UnionOverlap_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [return: UnionId("Customer", "Order")]
                public Guid GetId() => Guid.Empty;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(GetId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MethodReturnSource_UnionDisjoint_FiresSIA001()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [return: UnionId("Customer", "Supplier")]
                public Guid GetId() => Guid.Empty;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(GetId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task MethodReturnSource_UnwrapsAwait()
    {
        var source =
            """
            using System;

            using System.Threading.Tasks;

            public class Holder
            {
                [return: Id("Customer")]
                public Task<Guid> LoadCustomerIdAsync() => Task.FromResult(Guid.Empty);

                public void Consume([Id("Order")] Guid value) { }

                public async Task Use() => Consume(await LoadCustomerIdAsync());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task MethodReturnSource_InheritedFromOverride_FiresSIA001()
    {
        var source =
            """
            using System;

            public abstract class Base
            {
                [return: Id("Customer")]
                public abstract Guid GetId();
            }

            public class Derived : Base
            {
                public override Guid GetId() => Guid.Empty;
            }

            public class Holder
            {
                public void Consume([Id("Order")] Guid value) { }

                public void Use(Derived derived) => Consume(derived.GetId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task MethodReturnSource_InheritedFromInterface_FiresSIA001()
    {
        var source =
            """
            using System;

            public interface IGetter
            {
                [return: Id("Customer")]
                Guid GetId();
            }

            public class Impl : IGetter
            {
                public Guid GetId() => Guid.Empty;
            }

            public class Holder
            {
                public void Consume([Id("Order")] Guid value) { }

                public void Use(Impl impl) => Consume(impl.GetId());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task MissingSourceId_ArgumentToParameter()
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

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
        await Assert.That(diagnostics[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task MissingSourceId_PropertyAssignment()
    {
        var source =
            """
            using System;

            public class Source
            {
                public Guid Raw { get; set; }
            }

            public class Holder
            {
                public Guid OrderId { get; set; }

                public void Use(Source src) => OrderId = src.Raw;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
    }

    [Test]
    public async Task DroppedId_AssignPropertyToUnattributed()
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

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
        await Assert.That(diagnostics[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task DroppedId_ArgumentToParameter()
    {
        var source =
            """
            using System;

            public class Target
            {
                public void Consume(Guid value) { }
            }

            public class Holder
            {
                public Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
    }

    [Test]
    public async Task MatchingIds_NoDiagnostic()
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
                public Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LocalVariableSource_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Target
            {
                public void Consume([Id("Order")] Guid value) { }
            }

            public class Caller
            {
                public void Use(Target target)
                {
                    Guid local = Guid.NewGuid();
                    target.Consume(local);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NoIdAttributeAnywhere_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Target
            {
                public void Consume(Guid value) { }
            }

            public class Holder
            {
                public Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CaseDifference_IsMismatch()
    {
        // Unlike StringSyntax which special-cases the first character, Id values are
        // user-defined tags and are compared ordinally. "Order" vs "order" is a mismatch.
        var source =
            """
            using System;

            public class Target
            {
                public void Consume([Id("Order")] Guid value) { }
            }

            public class Holder
            {
                [Id("order")]
                public Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task FieldSource_Mismatch()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid CustomerField;

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(CustomerField);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task GuidStringInt_AllWorkWithAnyTag()
    {
        // The analyzer ignores the primitive type — it only compares the Id tag string.
        var source =
            """
            public class Holder
            {
                public int CustomerId { get; set; }

                public void Consume([Id("Order")] int value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task StringTypedId_MismatchFires()
    {
        // string implements IEnumerable<char>; the collection-tag suppression must not
        // treat string-typed Id members as element-tagged collections.
        var source =
            """
            public class Holder
            {
                [Id("Customer")]
                public string Value { get; set; } = "";

                public void Consume([Id("Order")] string value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task EqualityCheck_MismatchedIds_Fires()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId { get; set; }

                public Guid CustomerId { get; set; }

                public bool Check() => OrderId == CustomerId;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InequalityCheck_MismatchedIds_Fires()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId { get; set; }

                public Guid CustomerId { get; set; }

                public bool Check() => OrderId != CustomerId;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task EqualityCheck_MatchingIds_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Order")]
                public Guid A { get; set; }

                [Id("Order")]
                public Guid B { get; set; }

                public bool Check() => A == B;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EqualityCheck_OneSideMissing_FiresSIA002()
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

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
        await Assert.That(diagnostics[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task EqualityCheck_OneSideMissing_RightToLeft_FiresSIA002()
    {
        // Same as above but the tagged side is on the right — fixer should target the left.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Other { get; set; }

                public Guid OrderId { get; set; }

                public bool Check() => Other == OrderId;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
    }

    [Test]
    public async Task EqualityCheck_AgainstEmpty_NoDiagnostic()
    {
        // Comparing a tagged id to Guid.Empty or a literal is routine and must not fire.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId { get; set; }

                public bool IsSet() => OrderId != Guid.Empty;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DictionaryIndexer_LibraryTarget_NoDiagnostic()
    {
        // Passing a tagged Guid to Dictionary<Guid, T>.this[Guid] previously fired SIA003
        // because the indexer's parameter has no [Id]. Library-declared targets are now
        // suppressed.
        var source =
            """
            using System;

            using System.Collections.Generic;

            public class Holder
            {
                Dictionary<Guid, string> map = new();

                public Guid OrderId { get; set; }

                public string Lookup() => map[OrderId];
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GuidEquals_LibraryTarget_NoDiagnostic()
    {
        // Guid.Equals(Guid) and object.Equals(object) are library boundary methods —
        // they don't carry [Id] semantics, so passing a tagged value must not fire SIA003.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Order")]
                public Guid A { get; set; }

                [Id("Order")]
                public Guid B { get; set; }

                public bool Check() => A.Equals(B);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InheritedProperty_InheritsTagFromBase()
    {
        // Base has [Id("Order")]; derived inherits without redeclaring. Accessing
        // derived.Id resolves to the base property symbol, so this works naturally
        // (no hierarchy walk required). Pinned to guard against regressions.
        var source =
            """
            using System;

            public class Base
            {
                [Id("Order")]
                public Guid Id { get; set; }
            }

            public class Derived : Base { }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task OverriddenProperty_InheritsTagFromBase()
    {
        // Derived overrides without repeating [Id]. The analyzer walks OverriddenProperty
        // so passing derived.Id where [Id("Customer")] is expected still fires SIA001.
        var source =
            """
            using System;

            public class Base
            {
                [Id("Order")]
                public virtual Guid Id { get; set; }
            }

            public class Derived : Base
            {
                public override Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task ImplicitInterfaceProperty_InheritsTagFromInterface()
    {
        var source =
            """
            using System;

            public interface IEntity
            {
                [Id("Order")]
                Guid Id { get; }
            }

            public class Order : IEntity
            {
                public Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Order order) => target.Consume(order.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task ExplicitInterfaceProperty_InheritsTagFromInterface()
    {
        // Access via the interface type since explicit impls are not accessible on the
        // concrete type. The explicit-impl path of the hierarchy walk is exercised.
        var source =
            """
            using System;

            public interface IEntity
            {
                [Id("Order")]
                Guid Id { get; }
            }

            public class Order : IEntity
            {
                Guid IEntity.Id => Guid.Empty;
            }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, IEntity entity) => target.Consume(entity.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task NewHideProperty_DoesNotInheritTag()
    {
        // `new` hide is an explicit fresh declaration. The derived property has its own
        // (empty) attribute set and SHOULD NOT pick up the base's [Id]. Access via the
        // derived static type fires SIA001 — the derived's tag comes from the naming
        // convention ("Derived") and conflicts with the target's "Customer", not the
        // base's "Order".
        var source =
            """
            using System;

            public class Base
            {
                [Id("Order")]
                public Guid Id { get; set; }
            }

            public class Derived : Base
            {
                public new Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Derived")).IsTrue();
        await Assert.That(message.Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task OverriddenMethodParameter_InheritsTagFromBase()
    {
        // Abstract base has an explicit non-convention tag on the parameter so the rename
        // can't be satisfied by the naming convention. Override drops the attribute; call
        // through the derived receiver still sees SIA001 via the inheritance walk.
        var source =
            """
            using System;

            public abstract class Base
            {
                public abstract void Process([Id("Urgent")] Guid value);
            }

            public class Impl : Base
            {
                public override void Process(Guid value) { }
            }

            public class Consumer
            {
                public Guid CustomerId { get; set; }

                public void Use(Impl impl) => impl.Process(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task ObjectParameter_DoesNotFireSIA003()
    {
        // Logger-style helper: tagged id passed into an `object` parameter. The [Id] tag
        // is erased through `object`, so firing SIA003 here would be constant noise.
        var source =
            """
            using System;

            public class Logger
            {
                public void Log(string message, object value) { }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(Logger logger) => logger.Log("processing {0}", OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task GenericTypeParameter_DoesNotFireSIA003()
    {
        // Generic pass-through methods (identity, container helpers) can't carry tags.
        var source =
            """
            using System;

            public class Helper
            {
                public T Identity<T>(T value) => value;
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public Guid Use(Helper h) => h.Identity(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuppressedNamespace_DefaultSystem_NoSIA003()
    {
        // User-declared class in a `System.*` namespace lives in source, so the metadata
        // suppression doesn't cover it — only the namespace rule does.
        var source =
            """
            using System;

            namespace System.Fake
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(System.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuppressedNamespace_DefaultMicrosoft_NoSIA003()
    {
        var source =
            """
            using System;

            namespace Microsoft.Fake
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(Microsoft.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UserNamespace_NotSuppressed_StillFires()
    {
        var source =
            """
            using System;

            namespace MyCompany.Logging
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(MyCompany.Logging.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
    }

    [Test]
    public async Task SuppressedNamespace_CustomOverride_AppliesToConfiguredPrefix()
    {
        // With the option set, MyCompany.* should be suppressed while System.* should NOT
        // (user value fully replaces the defaults).
        var source =
            """
            using System;

            namespace MyCompany.Logging
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }

            namespace System.Fake
            {
                public class Target2
                {
                    public void Consume(Guid value) { }
                }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(MyCompany.Logging.Target m, System.Fake.Target2 s)
                {
                    m.Consume(OrderId);
                    s.Consume(OrderId);
                }
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "MyCompany*"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
        await Assert.That(diagnostics[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task SuppressedNamespace_EmptyOverride_DisablesAllSuppression()
    {
        // Empty value = no suppression. A user-declared class in System.Fake now fires.
        var source =
            """
            using System;

            namespace System.Fake
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }

            public class Consumer
            {
                public Guid OrderId { get; set; }

                public void Use(System.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = ""
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
    }

    [Test]
    public async Task Convention_IdOnType_InferredAsTypeName()
    {
        // Order.Id has no [Id] but the naming convention tags it "Order"; passing into an
        // [Id("Customer")] parameter fires SIA001 with "Order" vs "Customer".
        var source =
            """
            using System;

            public class Order
            {
                public Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Order order) => target.Consume(order.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Order")).IsTrue();
        await Assert.That(message.Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task Convention_XxxIdProperty_InferredAsPrefix()
    {
        // CustomerId -> convention "Customer"; passing into [Id("Order")] fires SIA001.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid CustomerId { get; set; }

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task Convention_CrossTypeMatch_NoDiagnostic()
    {
        // Customer.Id (convention "Customer") and Order.CustomerId (convention "Customer")
        // reference the same conceptual Id — the analyzer must accept matching flow.
        var source =
            """
            using System;

            public class Customer
            {
                public Guid Id { get; set; }
            }

            public class Order
            {
                public Guid CustomerId { get; set; }
            }

            public class Lookup
            {
                public Customer? Find([Id("Customer")] Guid id) => null;

                public Customer? For(Order order) => Find(order.CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Convention_FieldConvention_Applies()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public Guid OrderId;

                public void Consume([Id("Customer")] Guid value) { }

                public void Use() => Consume(OrderId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task Convention_NonIdName_NoConvention()
    {
        // "Value" isn't an Id-convention name, so no inferred tag — passing to an [Id]
        // parameter fires SIA002 (not SIA001).
        var source =
            """
            using System;

            public class Holder
            {
                public Guid Value { get; set; }

                public void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
    }

    [Test]
    public async Task Convention_ExplicitAttributeOverridesConvention()
    {
        // CustomerId would be "Customer" by convention, but explicit [Id("Special")] wins.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Special")]
                public Guid CustomerId { get; set; }

                public void Consume([Id("Customer")] Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Special")).IsTrue();
        await Assert.That(message.Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task SIA004_TwoTypesSameName_DifferentNamespaces_Fires()
    {
        // Two Order classes in separate namespaces both map to conventional name "Order".
        // SIA004 fires on each declaration.
        var source =
            """
            using System;

            namespace One
            {
                public class Order
                {
                    public Guid Id { get; set; }
                }
            }

            namespace Two
            {
                public class Order
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia004 = diagnostics.Where(_ => _.Id == "SIA004").ToArray();

        await Assert.That(sia004.Length).IsEqualTo(2);
        await Assert.That(sia004.All(_ => _.GetMessage().Contains("Order"))).IsTrue();
    }

    [Test]
    public async Task SIA004_NestedUnderDifferentParents_Fires()
    {
        var source =
            """
            using System;

            public class A
            {
                public class Foo
                {
                    public Guid Id { get; set; }
                }
            }

            public class B
            {
                public class Foo
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia004 = diagnostics.Where(_ => _.Id == "SIA004").ToArray();

        await Assert.That(sia004.Length).IsEqualTo(2);
    }

    [Test]
    public async Task SIA004_ExplicitAttributeOnOne_Disambiguates()
    {
        // Adding an explicit [Id("...")] (with a different value) on one declaration takes
        // it out of the ambiguity set — SIA004 no longer fires on either side.
        var source =
            """
            using System;

            namespace One
            {
                public class Order
                {
                    [Id("OneOrder")]
                    public Guid Id { get; set; }
                }
            }

            namespace Two
            {
                public class Order
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Count(_ => _.Id == "SIA004")).IsEqualTo(0);
    }

    [Test]
    public async Task SIA004_SameXxxIdOnDifferentTypes_NoDiagnostic()
    {
        // `XxxId` convention does not feed the ambiguity map: two types each having a
        // `CustomerId` property are expected and desirable.
        var source =
            """
            using System;

            public class Order
            {
                public Guid CustomerId { get; set; }
            }

            public class Invoice
            {
                public Guid CustomerId { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Count(_ => _.Id == "SIA004")).IsEqualTo(0);
    }

    [Test]
    public async Task SIA005_RedundantAttributeOnId_Fires()
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

        var diagnostics = await GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        await Assert.That(sia005.Length).IsEqualTo(1);
        await Assert.That(sia005[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task SIA005_RedundantAttributeOnXxxId_Fires()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid CustomerId { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        await Assert.That(sia005.Length).IsEqualTo(1);
        await Assert.That(sia005[0].GetMessage().Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_CarriesBaseAndDerivedTags_Match()
    {
        // child1.Id carries the set {"Child1", "Base"} — both parameters are satisfied.
        // (The parameter attributes are dropped because convention infers them.)
        var source =
            """
            using System;

            public class Base
            {
                public Guid Id { get; set; }
            }

            public class Child1 : Base;

            public class Holder
            {
                public static void Foo(Guid child1Id, Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Convention_InheritedId_SiblingDerivedType_FiresMismatch()
    {
        // child2.Id is {"Child2", "Base"} — satisfies the Base parameter but NOT the
        // Child1 parameter; SIA001 fires only on the first argument.
        var source =
            """
            using System;

            public class Base
            {
                public Guid Id { get; set; }
            }

            public class Child1 : Base;
            public class Child2 : Base;

            public class Holder
            {
                public static void Foo(Guid child1Id, Guid baseId) { }

                public void Use()
                {
                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Child2")).IsTrue();
        await Assert.That(message.Contains("Child1")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_StaticReceiverIsBase_DoesNotCarryDerivedTag()
    {
        // When the static receiver type is Base, the access carries only "Base" — the
        // derived-type tags aren't inferred because the caller didn't express them.
        var source =
            """
            using System;

            public class Base
            {
                public Guid Id { get; set; }
            }

            public class Child1 : Base;

            public class Holder
            {
                public static void TakeChild1(Guid child1Id) { }

                public void Use(Base b) => TakeChild1(b.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task Convention_InheritedId_AbstractClassWithExplicitAttributes_UnionsTags()
    {
        // Abstract class Base + override properties with explicit [Id] at every level.
        // Override chain walks Child1.Id -> Base.Id and unions both tags, so child1.Id
        // carries {"Child1","Base"} and satisfies parameters tagged either way while
        // child2.Id correctly fails the "Child1" parameter.
        var source =
            """
            using System;

            public abstract class Base
            {
                [Id("Base")]
                public abstract Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                [Id("Child1")]
                public override Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                [Id("Child2")]
                public override Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(Guid child1Id, Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        await Assert.That(flow.Length).IsEqualTo(1);
        await Assert.That(flow[0].Id).IsEqualTo("SIA001");
        var message = flow[0].GetMessage();
        await Assert.That(message.Contains("Child2")).IsTrue();
        await Assert.That(message.Contains("Child1")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_InterfaceWithExplicitAttributes_UnionsTags()
    {
        // Interface Base + class impls with explicit [Id] at every level. Implicit
        // interface implementation is walked, so child1.Id carries {"Child1","Base"}.
        var source =
            """
            using System;

            public interface Base
            {
                [Id("Base")]
                Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                [Id("Child1")]
                public Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                [Id("Child2")]
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(Guid child1Id, Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        await Assert.That(flow.Length).IsEqualTo(1);
        await Assert.That(flow[0].Id).IsEqualTo("SIA001");
        var message = flow[0].GetMessage();
        await Assert.That(message.Contains("Child2")).IsTrue();
        await Assert.That(message.Contains("Child1")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_InterfaceWithConventionOnly_UnionsTags()
    {
        // Interface Base with an abstract Id, children implement without explicit [Id].
        // Both convention tags (Child1/Child2 + Base) come from the interface walk.
        var source =
            """
            using System;

            public interface Base
            {
                Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                public Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(Guid child1Id, Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        await Assert.That(flow.Length).IsEqualTo(1);
        await Assert.That(flow[0].Id).IsEqualTo("SIA001");
        var message = flow[0].GetMessage();
        await Assert.That(message.Contains("Child2")).IsTrue();
        await Assert.That(message.Contains("Child1")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_MismatchMessage_ReceiverTypeFirst()
    {
        // treasuryBid.Id inherits Id from BaseEntity. The tag set must list the receiver
        // static type ("TreasuryBid") before the declaring type ("BaseEntity") so the
        // diagnostic message — and the FirstValue used by the code fix — prefers the
        // more specific name the user sees locally.
        var source =
            """
            using System;

            public class BaseEntity
            {
                public Guid Id { get; set; }
            }

            public class TreasuryBid : BaseEntity;

            public class Holder
            {
                public static void BuildTreasureMeasures([Id("Order")] Guid value) { }

                public void Use(TreasuryBid bid) => BuildTreasureMeasures(bid.Id);
            }
            """;

        var diagnostics = (await GetDiagnostics(source)).Where(_ => _.Id == "SIA001").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("TreasuryBid/BaseEntity")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_DeepChain_MismatchMessage_MostDerivedFirst()
    {
        // leaf.Id walks Leaf → Mid → Root. The resulting tag list must be
        // "Leaf/Mid/Root" (most-derived first) so code fixes pick the receiver type.
        var source =
            """
            using System;

            public class Root { public Guid Id { get; set; } }
            public class Mid : Root;
            public class Leaf : Mid;

            public class Holder
            {
                public static void TakeOther([Id("Other")] Guid value) { }

                public void Use(Leaf leaf) => TakeOther(leaf.Id);
            }
            """;

        var diagnostics = (await GetDiagnostics(source)).Where(_ => _.Id == "SIA001").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].GetMessage().Contains("Leaf/Mid/Root")).IsTrue();
    }

    [Test]
    public async Task Convention_InheritedId_DeepChain_IncludesAllAncestors()
    {
        // leaf.Id walks Leaf → Mid → Root and carries all three tags.
        var source =
            """
            using System;

            public class Root { public Guid Id { get; set; } }
            public class Mid : Root;
            public class Leaf : Mid;

            public class Holder
            {
                public static void TakeRoot(Guid rootId) { }
                public static void TakeMid(Guid midId) { }
                public static void TakeLeaf(Guid leafId) { }

                public void Use(Leaf leaf)
                {
                    TakeRoot(leaf.Id);
                    TakeMid(leaf.Id);
                    TakeLeaf(leaf.Id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Convention_ParameterCamelCase_InferredAsPascal()
    {
        // Parameter `orderId` (camelCase) -> "Order" tag; passing into a "Customer"-tagged
        // target fires SIA001 with "Order" vs "Customer".
        var source =
            """
            using System;

            public class Holder
            {
                public Guid CustomerId { get; set; }

                public static void ProcessOrder(Guid orderId) { }

                public void Trigger() => ProcessOrder(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Order")).IsTrue();
        await Assert.That(message.Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task Convention_ParameterBareId_NoConvention()
    {
        // A parameter named just `id` has no containing-type equivalent and must NOT
        // receive an inferred tag — otherwise generic helpers would be over-tagged.
        var source =
            """
            using System;

            public class Holder
            {
                public Guid CustomerId { get; set; }

                public static void Lookup(Guid id) { }

                public void Trigger() => Lookup(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
    }

    [Test]
    public async Task SuffixInference_Disabled_Parameter_FallsThroughToWholeNameRule()
    {
        // Default (off): `sourceProductId` is whole-name-inferred as "SourceProduct".
        // Passing a Product.Id ("Product") in fires SIA001 — the noise the opt-in feature
        // is designed to remove.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Duplicate(Guid sourceProductId) { }

                public void Trigger(Product p) => Duplicate(p.Id);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("SourceProduct")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_Parameter_MatchesKnownTag()
    {
        // Opt-in: `sourceProductId` / `targetProductId` both infer "Product" because
        // Product.Id is a known convention tag. Source tag matches → no diagnostic.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Duplicate(Guid sourceProductId, Guid targetProductId, string newName) { }

                public void Trigger(Product p) => Duplicate(p.Id, p.Id, "n");
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuffixInference_Enabled_Parameter_MismatchFiresSIA001()
    {
        // Opt-in: a `CustomerId` source flowing into `sourceProductId` crosses domains.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Customer
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Duplicate(Guid sourceProductId) { }

                public void Trigger(Customer c) => Duplicate(c.Id);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Product")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_UnknownWord_FallsThrough()
    {
        // `rawProductBytesId` — last word before `Id` is `Bytes`, which is not a known
        // tag. Suffix rule declines; the whole-name rule then infers "RawProductBytes".
        // Source Product → SIA001 Product vs RawProductBytes, confirming the suffix rule
        // did NOT silently tag the parameter as "Product".
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Store(Guid rawProductBytesId) { }

                public void Trigger(Product p) => Store(p.Id);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("RawProductBytes")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_LastWordWins()
    {
        // `productOrderId` — last word before `Id` is `Order`, so the param is tagged
        // "Order". Passing a Product.Id in fires SIA001 (Product vs Order).
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Order
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Process(Guid productOrderId) { }

                public void Trigger(Product p) => Process(p.Id);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_OnProperty()
    {
        // Properties on a command / DTO type: `SourceProductId` and `TargetProductId`
        // both infer "Product" via the suffix rule. Assigning each to a Product.Id is
        // clean; the string property `NewName` doesn't participate.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class DuplicateProductCommand
            {
                public Guid SourceProductId { get; set; }
                public Guid TargetProductId { get; set; }
                public string NewName { get; set; } = "";
            }

            public class Caller
            {
                public void Build(Product p, DuplicateProductCommand cmd)
                {
                    cmd.SourceProductId = p.Id;
                    cmd.TargetProductId = p.Id;
                }
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuffixInference_Enabled_OnField()
    {
        // Same rule on a plain field. `SourceProductId` infers "Product" with the flag on.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Slot
            {
                public Guid SourceProductId;
            }

            public class Caller
            {
                public void Fill(Product p, Slot s) => s.SourceProductId = p.Id;
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuffixInference_Disabled_PropertyFallsThrough()
    {
        // With the flag off, `SourceProductId` is whole-name-tagged "SourceProduct" via
        // rule 2. Assigning a Product.Id ("Product") into it fires SIA001 — the baseline
        // noise the opt-in removes.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class DuplicateProductCommand
            {
                public Guid SourceProductId { get; set; }
            }

            public class Caller
            {
                public void Build(Product p, DuplicateProductCommand cmd) => cmd.SourceProductId = p.Id;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("SourceProduct")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_ExplicitAttributeWins()
    {
        // Explicit `[Id("Customer")]` on the parameter overrides suffix inference.
        // Source is a Product.Id → fires SIA001 (Product vs Customer), not Product vs Product.
        var source =
            """
            using System;
            using StrongIdAnalyzer;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Customer
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Process([Id("Customer")] Guid sourceProductId) { }

                public void Trigger(Product p) => Process(p.Id);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        var sia001 = diagnostics.Where(_ => _.Id == "SIA001").ToArray();
        await Assert.That(sia001.Length).IsEqualTo(1);
        var message = sia001[0].GetMessage();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_MultiWordTail_MatchesKnownTag()
    {
        // `templateExternalObjectId` / `newExternalObjectId`: the last single word
        // ("Object") is not a known tag, but "ExternalObject" is. The walk continues
        // back to the next upper-case boundary and matches the multi-word tail.
        var source =
            """
            using System;

            public record ExternalObject(Guid Id);

            public class Service
            {
                public ExternalObject? Get(Guid externalObjectId) => null;
            }

            public class Holder
            {
                private readonly Service service = new();

                public void Run(Guid templateExternalObjectId, Guid newExternalObjectId)
                {
                    service.Get(templateExternalObjectId);
                    service.Get(newExternalObjectId);
                }
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuffixInference_Enabled_WholePrefixWinsOverInnerWord()
    {
        // `AccessGroupId` — both `AccessGroup` and `Group` are known tags. Longest-first
        // resolution prefers the exact whole-prefix tag over the inner-word `Group`.
        // Assigning an `AccessGroup` id to it is clean; passing a `Group` id fires SIA001.
        var source =
            """
            using System;

            public class Group
            {
                public Guid Id { get; set; }
            }

            public class AccessGroup
            {
                public Guid Id { get; set; }
            }

            public class AccessRule
            {
                public Guid AccessGroupId { get; set; }
            }

            public class Holder
            {
                public void AssignFromAccessGroup(AccessRule rule, AccessGroup ag) =>
                    rule.AccessGroupId = ag.Id;

                public void AssignFromGroup(AccessRule rule, Group g) =>
                    rule.AccessGroupId = g.Id;
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Group")).IsTrue();
        await Assert.That(message.Contains("AccessGroup")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_WholePrefixWinsOverInnerWord_IdInherited()
    {
        // Same shape as the AccessGroup/Group case but `AccessGroup` inherits `Id` from a
        // base type rather than declaring it directly. Both `Group` and `AccessGroup` must
        // qualify as known tags (the receiver-walk produces "AccessGroup" at the call site,
        // so KnownTags has to recognise inherited `Id` members too) — otherwise longest-first
        // matching falls through to "Group" and wrongly fires SIA001 on AccessGroup sources.
        var source =
            """
            using System;

            public abstract class BaseEntity
            {
                public Guid Id { get; set; }
            }

            public class Group : BaseEntity { }

            public class AccessGroup : BaseEntity { }

            public class AccessRule : BaseEntity
            {
                public Guid AccessGroupId { get; set; }
            }

            public class Holder
            {
                public void AssignFromAccessGroup(AccessRule rule, AccessGroup ag) =>
                    rule.AccessGroupId = ag.Id;

                public void AssignFromGroup(AccessRule rule, Group g) =>
                    rule.AccessGroupId = g.Id;
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Group")).IsTrue();
        await Assert.That(message.Contains("AccessGroup")).IsTrue();
    }

    [Test]
    public async Task SuffixInference_Enabled_WholePrefixWinsOverInnerWord_TypesInReferencedAssembly()
    {
        // Real-world shape from a downstream Seeding-style project: `AccessGroup` and
        // `Group` (both with `Id` via `BaseEntity`) live in the upstream assembly. The
        // consumer assembly has explicit `[Id<Group>]` attributes — so its source-only
        // KnownTags would have `"Group"` but not `"AccessGroup"`. Without walking the
        // referenced assembly's types, longest-first matching of the consumer's
        // `accessGroupId` parameter would skip `"AccessGroup"` (looks unknown) and fall
        // through to `"Group"`, then wrongly fire SIA001 against `AccessGroup` sources.
        var upstream =
            """
            using System;

            namespace Upstream;

            public abstract class BaseEntity
            {
                public Guid Id { get; set; }
            }

            public class Group : BaseEntity { }

            public class AccessGroup : BaseEntity { }
            """;

        var consumer =
            """
            using System;
            using StrongIdAnalyzer;
            using Upstream;

            public class Holder
            {
                [Id<Group>]
                public static readonly Guid SomeGroupId = Guid.NewGuid();

                public void AddUser(Guid accessGroupId) { }

                public void Trigger(AccessGroup ag) => AddUser(ag.Id);
            }
            """;

        var diagnostics = await GetCrossAssemblyDiagnosticsWithOptions(
            upstream,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SuffixInference_Enabled_WholePrefixUnknown_FallsBackToInnerWord()
    {
        // `productOrderId` — `ProductOrder` is NOT a known tag, so the longest-first walk
        // descends to `Order` (also known). Passing a `Product.Id` fires SIA001
        // (Product vs Order). Mirrors SuffixInference_Enabled_LastWordWins to lock in
        // that the descent still works when the whole prefix has no matching tag.
        var source =
            """
            using System;

            public class Product
            {
                public Guid Id { get; set; }
            }

            public class Order
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Process(Guid productOrderId) { }

                public void Trigger(Product p) => Process(p.Id);
            }
            """;

        var diagnostics = await GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task SIA005_RedundantAttributeOnParameter_Fires()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public static void Process([Id("Order")] Guid orderId) { }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        await Assert.That(sia005.Length).IsEqualTo(1);
        await Assert.That(sia005[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task SIA005_DifferentExplicitValue_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Order
            {
                [Id("Special")]
                public Guid Id { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Count(_ => _.Id == "SIA005")).IsEqualTo(0);
    }

    [Test]
    public async Task Union_SourceUnion_TargetSingleInOverlap_NoDiagnostic()
    {
        // [UnionId("Customer","Product")] source overlaps with [Id("Customer")] target.
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer", "Product")]
                public Guid Value { get; set; }

                public static void Consume([Id("Customer")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Union_SourceUnion_TargetSingleDisjoint_FiresSIA001()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer", "Product")]
                public Guid Value { get; set; }

                public static void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Product")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task Union_TargetUnion_SourceSingleInOverlap_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid CustomerValue { get; set; }

                public static void Consume([UnionId("Customer", "Product")] Guid value) { }

                public void Use() => Consume(CustomerValue);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Union_UnionAndUnionOverlap_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer", "Product")]
                public Guid SourceValue { get; set; }

                public static void Consume([UnionId("Product", "Order")] Guid value) { }

                public void Use() => Consume(SourceValue);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Union_UnionAndUnionDisjoint_FiresSIA001()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer", "Product")]
                public Guid SourceValue { get; set; }

                public static void Consume([UnionId("Order", "Supplier")] Guid value) { }

                public void Use() => Consume(SourceValue);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task Union_CoexistsWithConvention_UnionsTags()
    {
        // A property named `CustomerId` with explicit [UnionId("Order")] — convention
        // would give "Customer" and the explicit union adds "Order". Passing it to an
        // [Id("Order")] parameter must succeed because "Order" is in the set.
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Order")]
                public Guid CustomerId { get; set; }

                public static void Consume([Id("Order")] Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        await Assert.That(flow.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SIA006_SingletonUnion_Fires()
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

        var diagnostics = await GetDiagnostics(source);
        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();

        await Assert.That(sia006.Length).IsEqualTo(1);
        await Assert.That(sia006[0].GetMessage().Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task SIA006_SingletonUnion_OnParameter_Fires()
    {
        var source =
            """
            using System;

            public class Holder
            {
                public static void Consume([UnionId("Customer")] Guid value) { }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();

        await Assert.That(sia006.Length).IsEqualTo(1);
    }

    [Test]
    public async Task SIA006_MultiOptionUnion_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("Customer", "Product")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Count(_ => _.Id == "SIA006")).IsEqualTo(0);
    }

    [Test]
    public async Task SIA006_SingletonEmptyString_NoDiagnostic()
    {
        // `[UnionId("")]` is caught by SIA007 (empty tag is an error). SIA006
        // "singleton should be Id" would just recommend trading one invalid
        // shape for another, so it stays silent here.
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();
        await Assert.That(sia006.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SIA007_EmptyIdTag_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_WhitespaceIdTag_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id(" ")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_EmptyUnionOption_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_EmptyUnion_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId()]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_UnionWithOneEmpty_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId("", "Customer")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_UnionWithOneWhitespace_FiresError()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId(" ", "Customer")]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);
        var sia007 = diagnostics.Where(_ => _.Id == "SIA007").ToArray();

        await Assert.That(sia007.Length).IsEqualTo(1);
        await Assert.That(sia007[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task SIA007_NonEmptyTags_NoDiagnostic()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid One { get; set; }

                [UnionId("Customer", "Order")]
                public Guid Two { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Count(_ => _.Id == "SIA007")).IsEqualTo(0);
    }

    [Test]
    public async Task SIA006_EmptyUnion_NoDiagnostic()
    {
        // Regression: `[UnionId()]` (params array, zero values) previously tripped
        // the singleton path — the analyzer's length check was `> 1` (skip), so
        // length 0 fell through to "has only one option" with an empty "" value
        // and a codefix that produced `[Id("")]`. Only the exact length-1 case
        // is a collapsible singleton; zero-option unions should stay silent.
        var source =
            """
            using System;

            public class Holder
            {
                [UnionId()]
                public Guid Value { get; set; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();
        await Assert.That(sia006.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SIA005_MultiDeclaratorField_DoesNotFireRedundant()
    {
        // Regression: `[Id("Customer")] Guid CustomerId, OrderId;` applies to
        // BOTH declarators. The per-symbol SIA005 check fires only for
        // `CustomerId` (whose convention name matches the tag), and the codefix
        // strips the shared attribute — silently switching `OrderId` from the
        // explicit "Customer" tag to the "Order" value its name convention
        // implies. Multi-declarator fields must be skipped.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid CustomerId, OrderId;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();
        await Assert.That(sia005.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParamMatchingPropertyName_PrimaryCtor_IsSilent()
    {
        // The primary-ctor parameter `id` is the obvious carrier for the same-named
        // tagged property `Id`. Requiring an explicit [Id("Tenant")] on the parameter
        // would be noise — the user has already expressed intent via the name match.
        var source =
            """
            using System;

            public class Tenant(string id)
            {
                public string Id { get; } = id;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParamMatchingPropertyName_RegularCtor_IsSilent()
    {
        var source =
            """
            using System;

            public class Tenant
            {
                public Tenant(string id) => Id = id;
                public string Id { get; }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParamMatchingPropertyName_MethodSetter_IsSilent()
    {
        // Same name-correspondence rule applies outside constructors: a method param
        // `id` writing to property `Id` is just as obviously the carrier as in a ctor.
        var source =
            """
            using System;

            public class Order
            {
                public string Id { get; set; } = "";
                public void Reset(string id) => Id = id;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ParamMatchingPropertyName_NameMismatch_StillFires()
    {
        // Parameter name doesn't correspond to the property name — the suppression
        // doesn't apply, so SIA002 fires as before. The fix is to rename the parameter
        // (auto-tag via convention) or add [Id("Customer")] explicitly.
        var source =
            """
            using System;

            public class Holder
            {
                public void Update(string raw) => Value = raw;
                [Id("Customer")] public string Value { get; set; } = "";
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
    }

    [Test]
    public async Task ParamMatchingPropertyName_ExplicitTagMismatch_StillFiresSIA001()
    {
        // Even with names corresponding, an explicit tag on the parameter that
        // disagrees with the property's tag goes through SIA001 (mismatch), which the
        // suppression does NOT silence — source is tagged, not untagged.
        var source =
            """
            using System;

            public class Holder([Id("Customer")] string id)
            {
                [Id("Order")] public string Id { get; } = id;
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task AnonymousType_WriteIntoAnonProperty_IsSilent()
    {
        // Writes into anon-type properties have no fix site (anon members can't carry
        // [Id]) — the convention tag on the anon target is suppressed at report time so
        // the `BillId` ↔ `ProgramBillBase` mismatch produces no diagnostic.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("ProgramBillBase")]
                public Guid ProgramBillId { get; set; }

                public object Shape() => new { BillId = ProgramBillId };
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AnonymousType_ReadFromAnonProperty_FlowsConventionTag()
    {
        // Reading `extract.CustomerId` from an anonymous type carries the "Customer"
        // convention tag, so the assignment into a `[Id("Customer")]` target is allowed.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid Value { get; set; }

                public void Build(Guid raw)
                {
                    var anon = new { CustomerId = raw };
                    Value = anon.CustomerId;
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AnonymousType_ReadFromAnonProperty_MismatchedTagFires()
    {
        // The convention tag on the anon-property source ("Order") doesn't match the
        // [Id("Customer")] target — SIA001 fires as it would for any mismatched flow.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid Value { get; set; }

                public void Build(Guid raw)
                {
                    var anon = new { OrderId = raw };
                    Value = anon.OrderId;
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task RecordPrimaryCtorParameterAttribute_AppliesToGeneratedProperty()
    {
        var source =
            """
            using System;

            public record Holder([Id("Order")] Guid Value);

            public class Consumer
            {
                public void Consume([Id("Order")] Guid value) { }

                public void Use(Holder holder) => Consume(holder.Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RecordPrimaryCtorParameterAttribute_PropertyMismatchAgainstTarget()
    {
        var source =
            """
            using System;

            public record Holder([Id("Customer")] Guid Value);

            public class Consumer
            {
                public void Consume([Id("Order")] Guid value) { }

                public void Use(Holder holder) => Consume(holder.Value);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("Customer")).IsTrue();
        await Assert.That(message.Contains("Order")).IsTrue();
    }

    [Test]
    public async Task CrossAssembly_UnionIdOnReferencedProperty_FiresSIA003()
    {
        // Reproduces real-world cross-assembly usage: the UnionId-tagged property lives
        // in a separate assembly (messages package) with its own internal copy of the
        // generator-emitted UnionIdAttribute. The consumer assembly has its own copy
        // too. Matching the attribute by metadata name rather than symbol identity is
        // what keeps this working.
        // Names deliberately avoid the `Id`/`XxxId` naming convention so the convention
        // doesn't spuriously tag the untagged target — this isolates the cross-assembly
        // attribute-read path.
        var messagesSource =
            """
            using System;

            public class Message
            {
                [UnionId("Customer", "Order")]
                public Guid Subject { get; set; }
            }
            """;

        var consumerSource =
            """
            using System;

            public class Receiver
            {
                public Guid Value { get; set; }

                public void Copy(Message message) => Value = message.Subject;
            }
            """;

        var diagnostics = await GetCrossAssemblyDiagnostics(messagesSource, consumerSource);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task CrossAssembly_DerivedTagFlowsToBaseTarget_NoDiagnostic()
    {
        // Real-world case: derived/base types live in a referenced assembly (e.g. the
        // data-model package) while the consumer assembly sends a message that widens
        // via the base class. The type-name lookup must walk referenced metadata,
        // not just source declarations.
        var messagesSource =
            """
            using System;

            public abstract class ProgramBillBase {}
            public class ProgramBill : ProgramBillBase {}

            public record GenerateSnapshot([Id("ProgramBillBase")] Guid BillId);
            """;

        var consumerSource =
            """
            using System;

            public class Handler
            {
                [Id("ProgramBill")]
                public Guid Value { get; set; }

                public GenerateSnapshot Build() => new(Value);
            }
            """;

        var diagnostics = await GetCrossAssemblyDiagnostics(messagesSource, consumerSource);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LinqLambda_ParameterInheritsElementTag_Mismatch()
    {
        // `Values` carries `[Id("Customer")]` on an IEnumerable<Guid>. The `id` lambda
        // param in Select has no attribute but must inherit "Customer" so the mismatched
        // argument to ConsumeOrder fires SIA001. Names avoid the naming convention so
        // the test isolates the new LINQ inference path.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go() => Values.Select(id => { ConsumeOrder(id); return id; }).ToList();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
        await Assert.That(diagnostics[0].GetMessage().Contains("Order")).IsTrue();
    }

    [Test]
    public async Task LinqLambda_ParameterMatchesElementTag_NoDiagnostic()
    {
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void ConsumeCustomer([Id("Customer")] Guid value) { }

                public void Go() => Values.Select(id => { ConsumeCustomer(id); return id; }).ToList();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LinqLambda_ExpressionTreePredicate_ComparesAgainstTaggedParameter()
    {
        // Mirror of the GitHub issue: attributes aren't legal on lambdas inside
        // expression trees (CS8972), so lambda-param inference is the only way this
        // pattern can be checked. The `p` parameter in `.Any(p => p == needle)`
        // inherits "Product" from `o.Products`, and needle carries "Product"
        // explicitly, so the equality compiles clean. Parameter name deliberately
        // avoids the `XxxId` convention to keep SIA005 out of the picture.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Order
            {
                [Id("Product")]
                public IEnumerable<Guid> Products { get; set; } = null!;
            }

            public class Service
            {
                public bool Contains(IQueryable<Order> orders, [Id("Product")] Guid needle) =>
                    orders.Any(o => o.Products.Any(p => p == needle));
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LinqLambda_ExpressionTreePredicate_MismatchedTagFires()
    {
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Order
            {
                [Id("Product")]
                public IEnumerable<Guid> Products { get; set; } = null!;
            }

            public class Service
            {
                public bool Contains(IQueryable<Order> orders, [Id("Customer")] Guid needle) =>
                    orders.Any(o => o.Products.Any(p => p == needle));
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqFirst_ReturnsElementTag()
    {
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqChain_WhereThenFirst_PreservesElementTag()
    {
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.Where(x => x != Guid.Empty).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqSelect_IdentityLambda_PreservesElementTag()
    {
        // `.Select(x => x)` is an identity projection — the result element tag should
        // stay "Customer" so the mismatch against the Order target fires.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.Select(x => x).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqSelect_MethodGroupWithReturnId_UsesMethodTag()
    {
        // `.Select(MethodGroup)` where the method carries [return: Id("Order")] — the
        // chain's element tag should become "Order" regardless of the receiver's tag.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                [return: Id("Customer")]
                private static Guid ToOrderId(Guid v) => v;

                public void Copy() => Target = Values.Select(ToOrderId).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task LinqSelect_DropsElementTag_WhenSelectorChangesType()
    {
        // `.Select(id => Lookup(id))` changes the element type from Guid → string,
        // so the element tag should drop at the Select boundary. Lookup accepts a
        // Customer-tagged parameter so no SIA003 leaks from the selector; the
        // assignment of a string to a string target has no tag comparison to do.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public string Target { get; set; } = "";

                private static string Lookup([Id("Customer")] Guid v) => "";

                public void Copy() => Target = Values.Select(id => Lookup(id)).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ForEach_LoopVariableInheritsElementTag()
    {
        // Loop var has no attribute possibility in C#; the collection's element tag has
        // to flow through the foreach binding for `id` to carry "Customer".
        var source =
            """
            using System;
            using System.Collections.Generic;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go()
                {
                    foreach (var id in Values)
                    {
                        ConsumeOrder(id);
                    }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task ForEach_NestedOverProductIds_FlowsTag()
    {
        // Cross-check: the outer loop binds `order` (not a primitive, so no tagging),
        // and the inner loop binds `pid` to the "Product" tag from `order.Products`.
        // The equality comparison to the Customer-tagged needle should fire SIA001.
        var source =
            """
            using System;
            using System.Collections.Generic;

            public class Order
            {
                [Id("Product")]
                public IEnumerable<Guid> Products { get; set; } = null!;
            }

            public class Service
            {
                public bool Find(IEnumerable<Order> orders, [Id("Customer")] Guid needle)
                {
                    foreach (var order in orders)
                    {
                        foreach (var pid in order.Products)
                        {
                            if (pid == needle) return true;
                        }
                    }
                    return false;
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task UserDefinedExtension_ElementPreserving_PropagatesTag()
    {
        // Third-party LINQ-shape extension: `TakePage` takes IEnumerable<T> and returns
        // IEnumerable<T> with the same T. Shape-based matching means the Customer tag
        // survives the call, so `.First()` afterwards still fires SIA001 against Order.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public static class Paged
            {
                public static IEnumerable<T> TakePage<T>(this IEnumerable<T> source, int page, int size) =>
                    source.Skip(page * size).Take(size);
            }

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.TakePage(0, 10).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task UserDefinedExtension_LambdaParamInheritsElementTag()
    {
        // Custom extension shaped like `ForEach(IEnumerable<T>, Action<T>)` — the
        // lambda param `id` should inherit "Customer" from the receiver and then
        // mismatch against the Order-tagged consumer.
        var source =
            """
            using System;
            using System.Collections.Generic;

            public static class ForEachExt
            {
                public static void Each<T>(this IEnumerable<T> source, Action<T> callback)
                {
                    foreach (var item in source) callback(item);
                }
            }

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go() => Values.Each(id => ConsumeOrder(id));
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task Dictionary_ElementAccess_NotTagged()
    {
        // Explicit non-support note: multi-T containers carry no element tag. Even if
        // `[Id("Customer")]` sits on a Dictionary<Guid,string>, the foreach over its
        // KeyValuePair element type doesn't propagate — kv.Key stays Unknown.
        var source =
            """
            using System;
            using System.Collections.Generic;

            public class Holder
            {
                [Id("Customer")]
                public Dictionary<Guid, string> Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go()
                {
                    foreach (var kv in Values)
                    {
                        ConsumeOrder(kv.Key);
                    }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LinqLambda_ArrayReceiver_InheritsElementTag()
    {
        var source =
            """
            using System;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public Guid[] Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go() => Values.Select(id => { ConsumeOrder(id); return id; }).ToList();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqSelect_ExpressionBodiedLambda_UsesTaggedInvocation()
    {
        // Expression-bodied `Select(x => TaggedMethod(x))` — the lambda body resolves to
        // a tagged invocation, and that tag should become the new element tag flowing
        // out of the Select. No identity, no method group — the "tagged body" arm of
        // GetSelectElementTags.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Product")]
                public Guid Target { get; set; }

                [return: Id("Customer")]
                private static Guid Echo([Id("Customer")] Guid v) => v;

                public void Copy() => Target = Values.Select(x => Echo(x)).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
        await Assert.That(diagnostics[0].GetMessage().Contains("Product")).IsTrue();
    }

    [Test]
    public async Task CollectionTag_DoesNotLeakAsScalar_PassedToUntaggedCollectionParam()
    {
        // Direct assertion for SuppressCollectionTag: a [Id]-tagged collection passed
        // into a user-owned untagged IEnumerable parameter must not fire SIA003.
        // The tag on a collection-typed member is an element tag, not a scalar tag,
        // and the receiver slot of a LINQ-shape extension is itself a collection.
        var source =
            """
            using System;
            using System.Collections.Generic;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void Accept(IEnumerable<Guid> list) { }

                public void Go() => Accept(Values);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ArrayIndexer_ElementAccess_InheritsElementTag()
    {
        // Direct `arr[i]` — exercises IArrayElementReferenceOperation through
        // GetAccessInfo, which defers to GetReceiverElementTags on the array ref.
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid[] Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go() => ConsumeOrder(Values[0]);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    [Arguments("Single")]
    [Arguments("SingleOrDefault")]
    [Arguments("Last")]
    [Arguments("LastOrDefault")]
    [Arguments("ElementAt")]
    [Arguments("FirstOrDefault")]
    public async Task LinqElementReturning_AllNamedMethods_SurfaceElementTag(string methodName)
    {
        // `.ElementAt(0)` takes an int; the other operators in this case list are
        // parameterless. Supply a literal argument that satisfies either overload —
        // the extra int arg is ignored by Single/First/etc. because they accept
        // predicate overloads, so use the bare form unless the method needs an index.
        var call = methodName == "ElementAt"
            ? $"{methodName}(0)"
            : $"{methodName}()";

        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.{{call}};
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    [Arguments("OrderBy(x => x)")]
    [Arguments("Take(5)")]
    [Arguments("Skip(2)")]
    [Arguments("Distinct()")]
    [Arguments("Reverse()")]
    [Arguments("ToList()")]
    [Arguments("ToArray()")]
    [Arguments("ToHashSet()")]
    [Arguments("AsEnumerable()")]
    [Arguments("Append(Guid.Empty)")]
    [Arguments("Prepend(Guid.Empty)")]
    public async Task LinqElementPreserving_ChainPropagatesElementTag(string call)
    {
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Values.{{call}}.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task UserDefinedExtension_StaticFormCall_PropagatesTag()
    {
        // `Paged.TakePage(values, 0, 10)` — unreduced form, method.ReducedFrom is null,
        // Parameters[0] already includes the receiver slot. Hits the `?? method` arm of
        // GetExtensionReceiverType.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public static class Paged
            {
                public static IEnumerable<T> TakePage<T>(this IEnumerable<T> source, int page, int size) =>
                    source.Skip(page * size).Take(size);
            }

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy() => Target = Paged.TakePage(Values, 0, 10).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task UnionIdOnCollection_ElementInheritsAnyOption()
    {
        // `[UnionId("A","B")] IEnumerable<Guid>` — the element carries both tags, so
        // passing an element into a parameter tagged "A" alone matches, while "C"
        // mismatches.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [UnionId("Customer", "Admin")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public void AcceptCustomer([Id("Customer")] Guid value) { }
                public void AcceptProduct([Id("Product")] Guid value) { }

                public void Go()
                {
                    foreach (var id in Values)
                    {
                        AcceptCustomer(id);
                        AcceptProduct(id);
                    }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
        await Assert.That(diagnostics[0].GetMessage().Contains("Product")).IsTrue();
    }

    [Test]
    public async Task CollectionMember_ConventionTag_DoesNotApply()
    {
        // The convention-tagging rule (`Id` / `XxxId`) is deliberately skipped for
        // collection-typed members — `CustomerIds` does NOT pick up a "Customer" tag
        // just because of its name, since the analyzer can't prove the name refers to
        // the element vs. the container. Without that guard, passing `ids.First()` to
        // an Order-tagged parameter would produce a spurious SIA001.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                public IEnumerable<Guid> CustomerIds { get; set; } = null!;

                public void AcceptOrder([Id("Order")] Guid value) { }

                public void Go() => AcceptOrder(CustomerIds.First());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ForEach_OverArray_InheritsElementTag()
    {
        var source =
            """
            using System;

            public class Holder
            {
                [Id("Customer")]
                public Guid[] Values { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go()
                {
                    foreach (var id in Values)
                    {
                        ConsumeOrder(id);
                    }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InheritedTag_CollectionOnInterface_FlowsIntoLambda()
    {
        // Interface declares the [Id] on an IEnumerable<Guid>. Implementation doesn't
        // repeat the attribute. A LINQ lambda bound via the impl's static type should
        // still see "Customer" on the element.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public interface ICustomers
            {
                [Id("Customer")]
                IEnumerable<Guid> Ids { get; }
            }

            public class Impl : ICustomers
            {
                public IEnumerable<Guid> Ids { get; set; } = null!;
            }

            public class Holder
            {
                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go(Impl impl) =>
                    impl.Ids.Select(id => { ConsumeOrder(id); return id; }).ToList();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InheritedTag_CollectionOnAbstractBase_ForeachOverDerived()
    {
        // Abstract base declares [Id] on a virtual collection property; derived class
        // overrides without re-declaring the attribute. foreach over the derived view
        // should still see the base's tag on the element.
        var source =
            """
            using System;
            using System.Collections.Generic;

            public abstract class Base
            {
                [Id("Customer")]
                public abstract IEnumerable<Guid> Ids { get; }
            }

            public class Derived : Base
            {
                public override IEnumerable<Guid> Ids => [];
            }

            public class Holder
            {
                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Go(Derived derived)
                {
                    foreach (var id in derived.Ids)
                    {
                        ConsumeOrder(id);
                    }
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InheritedTag_ReturnIdOnInterfaceMethod_FlowsThroughImpl()
    {
        // [return: Id] on an interface method returning IEnumerable<T>. The
        // implementation has no attribute; element-flow through `.First()` on the
        // impl's result should still pick up the interface's tag.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public interface ISource
            {
                [return: Id("Customer")]
                IEnumerable<Guid> Load();
            }

            public class Impl : ISource
            {
                public IEnumerable<Guid> Load() => [];
            }

            public class Holder
            {
                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy(Impl impl) => Target = impl.Load().First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task InheritedTag_RecordPrimaryCtorParameterOnCollection()
    {
        // [Id] written on a record primary-ctor parameter whose type is a collection.
        // The compiler attaches the attribute to the parameter (default target); the
        // synthesized property needs the bridging logic to surface it for element flow.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public record Snapshot([Id("Customer")] IEnumerable<Guid> Values);

            public class Holder
            {
                [Id("Order")]
                public Guid Target { get; set; }

                public void Copy(Snapshot s) => Target = s.Values.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task LinqSelect_ChangingElementType_DropsTag_NoChainLeak()
    {
        // `.Select(id => id.ToString())` changes element type Guid → string.
        // Element tag drops at the Select boundary; the `.First()` result is an
        // untagged string, so no spurious SIA fires when it flows into an untagged
        // user-owned string target.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                [Id("Customer")]
                public IEnumerable<Guid> Values { get; set; } = null!;

                public string Target { get; set; } = "";

                public void Copy() => Target = Values.Select(id => id.ToString()).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_OnTypeParameter_TagsCollectionMember()
    {
        // `[IdTag]` on T means `WellKnownId<Operation>.Guids` picks up tag "Operation"
        // at the use site. `.First()` carries the element tag to the target, so an
        // explicit-tagged target is silent. `Target` intentionally does not match the
        // Id/XxxId convention so SIA005 (redundant attribute) does not interfere.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;

            public static class WellKnownId<[IdTag] T>
            {
                public static IEnumerable<Guid> Guids => null!;
            }

            public class Holder
            {
                [Id<Operation>]
                public Guid Target { get; set; }

                public void Copy() =>
                    Target = WellKnownId<Operation>.Guids.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_FlowsThroughExceptChain()
    {
        // Reproduces the CommitmentsDataModel case: the LINQ chain walks back through
        // Except (element-preserving) to WellKnownId<Operation>.Guids, which picks up
        // its implicit tag from `[IdTag] T`.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;

            public static class WellKnownId<[IdTag] T>
            {
                public static IEnumerable<Guid> Guids => null!;
            }

            public class Holder
            {
                [Id<Operation>]
                static Guid[] blocked = null!;

                [Id<Operation>]
                public Guid Target { get; set; }

                public void Copy() =>
                    Target = WellKnownId<Operation>.Guids.Except(blocked).First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_Mismatch_ReportsSIA001()
    {
        // Tag substitution must actually kick in — if the use site is
        // `WellKnownId<Operation>` but the target is `[Id<Customer>]`, SIA001 fires.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;
            public class Customer;

            public static class WellKnownId<[IdTag] T>
            {
                public static IEnumerable<Guid> Guids => null!;
            }

            public class Holder
            {
                [Id<Customer>]
                public Guid Target { get; set; }

                public void Copy() =>
                    Target = WellKnownId<Operation>.Guids.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task IdTag_ScalarMethodReturn_ProducesNoImplicitTag()
    {
        // Scalar method returns are out of scope for implicit [IdTag] flow — otherwise
        // factory methods (`MakeGuid`, `NewX`) inside a tagged generic would force
        // every caller storing the result into an untagged field to add [Id] too.
        // A call-site assignment into an unrelated-tag target stays silent.
        var source =
            """
            using System;

            public class Operation;
            public class Customer;

            public static class WellKnownId<[IdTag] T>
            {
                public static Guid MakeGuid(int index) => Guid.Empty;
            }

            public class Holder
            {
                [Id<Customer>]
                public Guid Target { get; set; }

                public void Copy() => Target = WellKnownId<Operation>.MakeGuid(0);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_MethodParameter_ProducesNoImplicitTag()
    {
        // Parameters of members inside a `[IdTag]`-tagged generic stay untagged. The
        // caller's tagged argument landing in an untagged target is SIA003 territory —
        // that's the pre-existing policy and is unchanged by [IdTag].
        var source =
            """
            using System;

            public class Operation;
            public class Customer;

            public static class WellKnownId<[IdTag] T>
            {
                public static string GetName(Guid id) => "";
            }

            public class Holder
            {
                [Id<Customer>]
                public Guid Source { get; set; }

                public string Use() => WellKnownId<Operation>.GetName(Source);
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA003");
    }

    [Test]
    public async Task IdTag_OpenGenericSelfReference_ProducesNoTag()
    {
        // Inside WellKnownId<T> itself T is still a type parameter — no real tag name
        // is available, so the analyzer must not infer anything. Cross-assigning the
        // untagged Guids to an untagged Guid target is silent.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public static class WellKnownId<[IdTag] T>
            {
                public static IEnumerable<Guid> Guids => null!;

                public static Guid First() => Guids.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_MultipleTagParameters_ProducesUnion()
    {
        // Two `[IdTag]` type parameters produce a union tag set on the collection
        // member. `.First()` inherits the set, so assigning to a target tagged with
        // either one is silent.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;
            public class Customer;
            public class Order;

            public static class Cross<[IdTag] T1, [IdTag] T2>
            {
                public static IEnumerable<Guid> Values => null!;
            }

            public class Holder
            {
                [Id<Customer>]
                public Guid Target { get; set; }

                public void Copy() => Target = Cross<Operation, Customer>.Values.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IdTag_NoAttribute_ProducesNoTag()
    {
        // Without `[IdTag]`, a plain generic type parameter must NOT leak a tag.
        // The `.Select(_ => new Holder { Target = _ })` shape forces the lambda param
        // resolution path: receiver Guids is untagged, so `_` falls back to NotPresent,
        // and the assignment to `[Id<Operation>] Target` fires SIA002.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;

            public static class WellKnownId<T>
            {
                public static IEnumerable<Guid> Guids => null!;
            }

            public class Holder
            {
                [Id<Operation>]
                public Guid Target { get; set; }

                public void Use() =>
                    WellKnownId<Operation>.Guids
                        .Select(_ => new Holder { Target = _ });
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA002");
    }

    [Test]
    public async Task IdTag_NestedGeneric_PicksUpOuterTag()
    {
        // A non-generic nested type inside Outer<[IdTag] T> inherits T's substitution
        // via ContainingType walking, so a collection member on Inner picks up the
        // outer tag. `.First()` carries the element tag to the assignment target.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Operation;

            public static class Outer<[IdTag] T>
            {
                public static class Inner
                {
                    public static IEnumerable<Guid> Values => null!;
                }
            }

            public class Holder
            {
                [Id<Operation>]
                public Guid Target { get; set; }

                public void Copy() => Target = Outer<Operation>.Inner.Values.First();
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LocalFromAwaitedAsyncLinqSelect_PropagatesProjectionTag()
    {
        // `var id = await q.Select(_ => _.Tagged).SingleAsync();` — the tag on
        // Row.CustomerId should survive the Select + await + SingleAsync and
        // reach the consumer. Stand in for EF Core's IQueryable async ext.
        var source =
            """
            using System;
            using System.Linq;
            using System.Threading.Tasks;

            public static class AsyncLinq
            {
                public static Task<T> SingleAsync<T>(this IQueryable<T> source) => Task.FromResult(default(T)!);
            }

            public class Row
            {
                public Guid CustomerId { get; set; }
            }

            public class Holder
            {
                public IQueryable<Row> Rows { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public async Task Use()
                {
                    var id = await Rows.Select(_ => _.CustomerId).SingleAsync();
                    ConsumeOrder(id);
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    [Test]
    public async Task AsyncLinqElementReturn_MatchingTag_NoDiagnostic()
    {
        // FirstAsync returning Task<Guid> over an IQueryable<Guid> tagged
        // [Id("Customer")] should flow the tag through to a matching target.
        var source =
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;

            public static class AsyncLinq
            {
                public static Task<T> FirstAsync<T>(this IQueryable<T> source) => Task.FromResult(default(T)!);
            }

            public class Holder
            {
                [Id("Customer")]
                public IQueryable<Guid> Ids { get; set; } = null!;

                public void ConsumeCustomer([Id("Customer")] Guid value) { }

                public async Task Use()
                {
                    ConsumeCustomer(await Ids.FirstAsync());
                }
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task IQueryable_SelectLambda_PropagatesTagToFirst()
    {
        // IQueryable<T>.Select takes an expression-tree lambda — the selector
        // surfaces directly as IAnonymousFunctionOperation after unwrap (not
        // wrapped in IDelegateCreationOperation). The tag on Row.CustomerId
        // must still flow through the projection + .First().
        var source =
            """
            using System;
            using System.Linq;

            public class Row
            {
                public Guid CustomerId { get; set; }
            }

            public class Holder
            {
                public IQueryable<Row> Rows { get; set; } = null!;

                public void ConsumeOrder([Id("Order")] Guid value) { }

                public void Use() => ConsumeOrder(Rows.Select(_ => _.CustomerId).First());
            }
            """;

        var diagnostics = await GetDiagnostics(source);

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Id).IsEqualTo("SIA001");
    }

    static Task<ImmutableArray<Diagnostic>> GetCrossAssemblyDiagnostics(
        string messagesSource,
        string consumerSource) =>
        GetCrossAssemblyDiagnosticsWithOptions(
            messagesSource,
            consumerSource,
            new Dictionary<string, string>());

    static Task<ImmutableArray<Diagnostic>> GetCrossAssemblyDiagnosticsWithOptions(
        string messagesSource,
        string consumerSource,
        IDictionary<string, string> globalOptions)
    {
        var messagesBase = CSharpCompilation.Create(
            "Messages",
            [CSharpSyntaxTree.ParseText(messagesSource)],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver
            .Create(new IdAttributeGenerator())
            .RunGeneratorsAndUpdateCompilation(messagesBase, out var messagesCompilation, out _);

        using var messagesStream = new MemoryStream();
        var emit = messagesCompilation.Emit(messagesStream);
        if (!emit.Success)
        {
            var errors = string.Join("\n", emit.Diagnostics.Where(_ => _.Severity == DiagnosticSeverity.Error));
            throw new($"Messages compilation failed:\n{errors}");
        }
        messagesStream.Position = 0;
        var messagesReference = MetadataReference.CreateFromStream(messagesStream);

        var consumerBase = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(consumerSource)],
            [..TrustedReferences.All, messagesReference],
            new(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver
            .Create(new IdAttributeGenerator())
            .RunGeneratorsAndUpdateCompilation(consumerBase, out var consumerCompilation, out _);

        var analyzerOptions = new AnalyzerOptions(
            [],
            new TestAnalyzerConfigOptionsProvider(globalOptions));

        return consumerCompilation
            .WithAnalyzers([new IdMismatchAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    static Task<ImmutableArray<Diagnostic>> GetDiagnostics(string source) =>
        GetDiagnosticsWithOptions(source, new Dictionary<string, string>());

    static Task<ImmutableArray<Diagnostic>> GetDiagnosticsWithOptions(
        string source,
        IDictionary<string, string> globalOptions)
    {
        var compilation = BuildCompilation(source);

        // Run the generator so StrongIdAnalyzer.IdAttribute exists in the compilation.
        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var analyzer = new IdMismatchAnalyzer();
        var analyzerOptions = new AnalyzerOptions(
            [],
            new TestAnalyzerConfigOptionsProvider(globalOptions));

        return updated
            .WithAnalyzers([analyzer], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    sealed class TestAnalyzerConfigOptions(IDictionary<string, string> options)
        : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (options.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }

            value = null!;
            return false;
        }
    }

    sealed class TestAnalyzerConfigOptionsProvider(IDictionary<string, string> options)
        : AnalyzerConfigOptionsProvider
    {
        readonly TestAnalyzerConfigOptions globals = new(options);

        public override AnalyzerConfigOptions GlobalOptions =>
            globals;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
            globals;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            globals;
    }

    static CSharpCompilation BuildCompilation(string source) =>
        CSharpCompilation.Create(
            "Tests",
            [CSharpSyntaxTree.ParseText(source)],
            TrustedReferences.All,
            new(OutputKind.DynamicallyLinkedLibrary));
}
