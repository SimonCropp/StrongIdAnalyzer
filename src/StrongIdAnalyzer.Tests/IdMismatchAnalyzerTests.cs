[TestFixture]
public class IdMismatchAnalyzerTests
{
    [Test]
    public void IdMismatch_ArgumentToParameter()
    {
        var source = """
            public class Target
            {
                public void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                [Id("Customer")]
                public System.Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Customer"));
        IsTrue(message.Contains("Order"));
    }

    [Test]
    public void IdMismatch_PropertyToProperty_Assignment()
    {
        // OrderId / CustomerId are auto-tagged by the naming convention.
        var source =
            """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public System.Guid CustomerId { get; set; }

                public void Copy() => OrderId = CustomerId;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void IdMismatch_ObjectInitializer()
    {
        var source =
            """
            public class Target
            {
                public System.Guid OrderId { get; set; }
            }

            public class Holder
            {
                public System.Guid CustomerId { get; set; }

                public Target Create() => new Target { OrderId = CustomerId };
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void MethodReturnSource_IsUnknown()
    {
        var source =
            """
            public class Holder
            {
                public System.Guid GetValue() => System.Guid.Empty;

                public void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(GetValue());
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void MissingSourceId_ArgumentToParameter()
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

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA002", diagnostics[0].Id);
        IsTrue(diagnostics[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void MissingSourceId_PropertyAssignment()
    {
        var source =
            """
            public class Source
            {
                public System.Guid Raw { get; set; }
            }

            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public void Use(Source src) => OrderId = src.Raw;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA002", diagnostics[0].Id);
    }

    [Test]
    public void DroppedId_AssignPropertyToUnattributed()
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

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
        IsTrue(diagnostics[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void DroppedId_ArgumentToParameter()
    {
        var source =
            """
            public class Target
            {
                public void Consume(System.Guid value) { }
            }

            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
    }

    [Test]
    public void MatchingIds_NoDiagnostic()
    {
        var source = """
            public class Target
            {
                public void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void LocalVariableSource_NoDiagnostic()
    {
        var source = """
            public class Target
            {
                public void Consume([Id("Order")] System.Guid value) { }
            }

            public class Caller
            {
                public void Use(Target target)
                {
                    System.Guid local = System.Guid.NewGuid();
                    target.Consume(local);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void NoIdAttributeAnywhere_NoDiagnostic()
    {
        var source = """
            public class Target
            {
                public void Consume(System.Guid value) { }
            }

            public class Holder
            {
                public System.Guid Value { get; set; }

                public void Use(Target target) => target.Consume(Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void CaseDifference_IsMismatch()
    {
        // Unlike StringSyntax which special-cases the first character, Id values are
        // user-defined tags and are compared ordinally. "Order" vs "order" is a mismatch.
        var source = """
            public class Target
            {
                public void Consume([Id("Order")] System.Guid value) { }
            }

            public class Holder
            {
                [Id("order")]
                public System.Guid OrderId { get; set; }

                public void Use(Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void FieldSource_Mismatch()
    {
        var source = """
            public class Holder
            {
                [Id("Customer")]
                public System.Guid CustomerField;

                public void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(CustomerField);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void GuidStringInt_AllWorkWithAnyTag()
    {
        // The analyzer ignores the primitive type — it only compares the Id tag string.
        var source = """
            public class Holder
            {
                public int CustomerId { get; set; }

                public void Consume([Id("Order")] int value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void EqualityCheck_MismatchedIds_Fires()
    {
        var source = """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public System.Guid CustomerId { get; set; }

                public bool Check() => OrderId == CustomerId;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void InequalityCheck_MismatchedIds_Fires()
    {
        var source = """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public System.Guid CustomerId { get; set; }

                public bool Check() => OrderId != CustomerId;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void EqualityCheck_MatchingIds_NoDiagnostic()
    {
        var source = """
            public class Holder
            {
                [Id("Order")]
                public System.Guid A { get; set; }

                [Id("Order")]
                public System.Guid B { get; set; }

                public bool Check() => A == B;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void EqualityCheck_OneSideMissing_FiresSIA002()
    {
        var source = """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public System.Guid Other { get; set; }

                public bool Check() => OrderId == Other;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA002", diagnostics[0].Id);
        IsTrue(diagnostics[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void EqualityCheck_OneSideMissing_RightToLeft_FiresSIA002()
    {
        // Same as above but the tagged side is on the right — fixer should target the left.
        var source = """
            public class Holder
            {
                public System.Guid Other { get; set; }

                public System.Guid OrderId { get; set; }

                public bool Check() => Other == OrderId;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA002", diagnostics[0].Id);
    }

    [Test]
    public void EqualityCheck_AgainstEmpty_NoDiagnostic()
    {
        // Comparing a tagged id to Guid.Empty or a literal is routine and must not fire.
        var source = """
            public class Holder
            {
                public System.Guid OrderId { get; set; }

                public bool IsSet() => OrderId != System.Guid.Empty;
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void DictionaryIndexer_LibraryTarget_NoDiagnostic()
    {
        // Passing a tagged Guid to Dictionary<Guid, T>.this[Guid] previously fired SIA003
        // because the indexer's parameter has no [Id]. Library-declared targets are now
        // suppressed.
        var source = """
            using System.Collections.Generic;

            public class Holder
            {
                Dictionary<System.Guid, string> map = new();

                public System.Guid OrderId { get; set; }

                public string Lookup() => map[OrderId];
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void GuidEquals_LibraryTarget_NoDiagnostic()
    {
        // Guid.Equals(Guid) and object.Equals(object) are library boundary methods —
        // they don't carry [Id] semantics, so passing a tagged value must not fire SIA003.
        var source = """
            public class Holder
            {
                [Id("Order")]
                public System.Guid A { get; set; }

                [Id("Order")]
                public System.Guid B { get; set; }

                public bool Check() => A.Equals(B);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void InheritedProperty_InheritsTagFromBase()
    {
        // Base has [Id("Order")]; derived inherits without redeclaring. Accessing
        // derived.Id resolves to the base property symbol, so this works naturally
        // (no hierarchy walk required). Pinned to guard against regressions.
        var source = """
            public class Base
            {
                [Id("Order")]
                public System.Guid Id { get; set; }
            }

            public class Derived : Base { }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void OverriddenProperty_InheritsTagFromBase()
    {
        // Derived overrides without repeating [Id]. The analyzer walks OverriddenProperty
        // so passing derived.Id where [Id("Customer")] is expected still fires SIA001.
        var source = """
            public class Base
            {
                [Id("Order")]
                public virtual System.Guid Id { get; set; }
            }

            public class Derived : Base
            {
                public override System.Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void ImplicitInterfaceProperty_InheritsTagFromInterface()
    {
        var source = """
            public interface IEntity
            {
                [Id("Order")]
                System.Guid Id { get; }
            }

            public class Order : IEntity
            {
                public System.Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Order order) => target.Consume(order.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void ExplicitInterfaceProperty_InheritsTagFromInterface()
    {
        // Access via the interface type since explicit impls are not accessible on the
        // concrete type. The explicit-impl path of the hierarchy walk is exercised.
        var source = """
            public interface IEntity
            {
                [Id("Order")]
                System.Guid Id { get; }
            }

            public class Order : IEntity
            {
                System.Guid IEntity.Id => System.Guid.Empty;
            }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, IEntity entity) => target.Consume(entity.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void NewHideProperty_DoesNotInheritTag()
    {
        // `new` hide is an explicit fresh declaration. The derived property has its own
        // (empty) attribute set and SHOULD NOT pick up the base's [Id]. Access via the
        // derived static type fires SIA001 — the derived's tag comes from the naming
        // convention ("Derived") and conflicts with the target's "Customer", not the
        // base's "Order".
        var source = """
            public class Base
            {
                [Id("Order")]
                public System.Guid Id { get; set; }
            }

            public class Derived : Base
            {
                public new System.Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Derived derived) => target.Consume(derived.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Derived"));
        IsTrue(message.Contains("Customer"));
    }

    [Test]
    public void OverriddenMethodParameter_InheritsTagFromBase()
    {
        // Abstract base has an explicit non-convention tag on the parameter so the rename
        // can't be satisfied by the naming convention. Override drops the attribute; call
        // through the derived receiver still sees SIA001 via the inheritance walk.
        var source = """
            public abstract class Base
            {
                public abstract void Process([Id("Urgent")] System.Guid value);
            }

            public class Impl : Base
            {
                public override void Process(System.Guid value) { }
            }

            public class Consumer
            {
                public System.Guid CustomerId { get; set; }

                public void Use(Impl impl) => impl.Process(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void ObjectParameter_DoesNotFireSIA003()
    {
        // Logger-style helper: tagged id passed into an `object` parameter. The [Id] tag
        // is erased through `object`, so firing SIA003 here would be constant noise.
        var source = """
            public class Logger
            {
                public void Log(string message, object value) { }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(Logger logger) => logger.Log("processing {0}", OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void GenericTypeParameter_DoesNotFireSIA003()
    {
        // Generic pass-through methods (identity, container helpers) can't carry tags.
        var source = """
            public class Helper
            {
                public T Identity<T>(T value) => value;
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public System.Guid Use(Helper h) => h.Identity(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void SuppressedNamespace_DefaultSystem_NoSIA003()
    {
        // User-declared class in a `System.*` namespace lives in source, so the metadata
        // suppression doesn't cover it — only the namespace rule does.
        var source = """
            namespace System.Fake
            {
                public class Target
                {
                    public void Consume(System.Guid value) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(System.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void SuppressedNamespace_DefaultMicrosoft_NoSIA003()
    {
        var source = """
            namespace Microsoft.Fake
            {
                public class Target
                {
                    public void Consume(System.Guid value) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(Microsoft.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void UserNamespace_NotSuppressed_StillFires()
    {
        var source = """
            namespace MyCompany.Logging
            {
                public class Target
                {
                    public void Consume(System.Guid value) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(MyCompany.Logging.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
    }

    [Test]
    public void SuppressedNamespace_CustomOverride_AppliesToConfiguredPrefix()
    {
        // With the option set, MyCompany.* should be suppressed while System.* should NOT
        // (user value fully replaces the defaults).
        var source = """
            namespace MyCompany.Logging
            {
                public class Target
                {
                    public void Consume(System.Guid value) { }
                }
            }

            namespace System.Fake
            {
                public class Target2
                {
                    public void Consume(System.Guid value) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(MyCompany.Logging.Target m, System.Fake.Target2 s)
                {
                    m.Consume(OrderId);
                    s.Consume(OrderId);
                }
            }
            """;

        var diagnostics = GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "MyCompany*"
            });

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
        IsTrue(diagnostics[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void SuppressedNamespace_EmptyOverride_DisablesAllSuppression()
    {
        // Empty value = no suppression. A user-declared class in System.Fake now fires.
        var source = """
            namespace System.Fake
            {
                public class Target
                {
                    public void Consume(System.Guid value) { }
                }
            }

            public class Consumer
            {
                public System.Guid OrderId { get; set; }

                public void Use(System.Fake.Target target) => target.Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnosticsWithOptions(
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = ""
            });

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
    }

    [Test]
    public void Convention_IdOnType_InferredAsTypeName()
    {
        // Order.Id has no [Id] but the naming convention tags it "Order"; passing into an
        // [Id("Customer")] parameter fires SIA001 with "Order" vs "Customer".
        var source = """
            public class Order
            {
                public System.Guid Id { get; set; }
            }

            public class Target
            {
                public void Consume([Id("Customer")] System.Guid value) { }
            }

            public class Consumer
            {
                public void Use(Target target, Order order) => target.Consume(order.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Order"));
        IsTrue(message.Contains("Customer"));
    }

    [Test]
    public void Convention_XxxIdProperty_InferredAsPrefix()
    {
        // CustomerId -> convention "Customer"; passing into [Id("Order")] fires SIA001.
        var source = """
            public class Holder
            {
                public System.Guid CustomerId { get; set; }

                public void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void Convention_CrossTypeMatch_NoDiagnostic()
    {
        // Customer.Id (convention "Customer") and Order.CustomerId (convention "Customer")
        // reference the same conceptual Id — the analyzer must accept matching flow.
        var source = """
            public class Customer
            {
                public System.Guid Id { get; set; }
            }

            public class Order
            {
                public System.Guid CustomerId { get; set; }
            }

            public class Lookup
            {
                public Customer? Find([Id("Customer")] System.Guid id) => null;

                public Customer? For(Order order) => Find(order.CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void Convention_FieldConvention_Applies()
    {
        var source = """
            public class Holder
            {
                public System.Guid OrderId;

                public void Consume([Id("Customer")] System.Guid value) { }

                public void Use() => Consume(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void Convention_NonIdName_NoConvention()
    {
        // "Value" isn't an Id-convention name, so no inferred tag — passing to an [Id]
        // parameter fires SIA002 (not SIA001).
        var source = """
            public class Holder
            {
                public System.Guid Value { get; set; }

                public void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA002", diagnostics[0].Id);
    }

    [Test]
    public void Convention_ExplicitAttributeOverridesConvention()
    {
        // CustomerId would be "Customer" by convention, but explicit [Id("Special")] wins.
        var source = """
            public class Holder
            {
                [Id("Special")]
                public System.Guid CustomerId { get; set; }

                public void Consume([Id("Customer")] System.Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Special"));
        IsTrue(message.Contains("Customer"));
    }

    [Test]
    public void SIA004_TwoTypesSameName_DifferentNamespaces_Fires()
    {
        // Two Order classes in separate namespaces both map to conventional name "Order".
        // SIA004 fires on each declaration.
        var source = """
            namespace One
            {
                public class Order
                {
                    public System.Guid Id { get; set; }
                }
            }

            namespace Two
            {
                public class Order
                {
                    public System.Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia004 = diagnostics.Where(_ => _.Id == "SIA004").ToArray();

        AreEqual(2, sia004.Length);
        IsTrue(sia004.All(_ => _.GetMessage().Contains("Order")));
    }

    [Test]
    public void SIA004_NestedUnderDifferentParents_Fires()
    {
        var source = """
            public class A
            {
                public class Foo
                {
                    public System.Guid Id { get; set; }
                }
            }

            public class B
            {
                public class Foo
                {
                    public System.Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia004 = diagnostics.Where(_ => _.Id == "SIA004").ToArray();

        AreEqual(2, sia004.Length);
    }

    [Test]
    public void SIA004_ExplicitAttributeOnOne_Disambiguates()
    {
        // Adding an explicit [Id("...")] (with a different value) on one declaration takes
        // it out of the ambiguity set — SIA004 no longer fires on either side.
        var source = """
            namespace One
            {
                public class Order
                {
                    [Id("OneOrder")]
                    public System.Guid Id { get; set; }
                }
            }

            namespace Two
            {
                public class Order
                {
                    public System.Guid Id { get; set; }
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Count(_ => _.Id == "SIA004"));
    }

    [Test]
    public void SIA004_SameXxxIdOnDifferentTypes_NoDiagnostic()
    {
        // `XxxId` convention does not feed the ambiguity map: two types each having a
        // `CustomerId` property are expected and desirable.
        var source = """
            public class Order
            {
                public System.Guid CustomerId { get; set; }
            }

            public class Invoice
            {
                public System.Guid CustomerId { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Count(_ => _.Id == "SIA004"));
    }

    [Test]
    public void SIA005_RedundantAttributeOnId_Fires()
    {
        var source = """
            public class Order
            {
                [Id("Order")]
                public System.Guid Id { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        AreEqual(1, sia005.Length);
        IsTrue(sia005[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void SIA005_RedundantAttributeOnXxxId_Fires()
    {
        var source = """
            public class Holder
            {
                [Id("Customer")]
                public System.Guid CustomerId { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        AreEqual(1, sia005.Length);
        IsTrue(sia005[0].GetMessage().Contains("Customer"));
    }

    [Test]
    public void Convention_InheritedId_CarriesBaseAndDerivedTags_Match()
    {
        // child1.Id carries the set {"Child1", "Base"} — both parameters are satisfied.
        // (The parameter attributes are dropped because convention infers them.)
        var source = """
            public class Base
            {
                public System.Guid Id { get; set; }
            }

            public class Child1 : Base;

            public class Holder
            {
                public static void Foo(System.Guid child1Id, System.Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(
            0,
            diagnostics.Length,
            string.Join("\n", diagnostics.Select(_ => _.Id + ": " + _.GetMessage())));
    }

    [Test]
    public void Convention_InheritedId_SiblingDerivedType_FiresMismatch()
    {
        // child2.Id is {"Child2", "Base"} — satisfies the Base parameter but NOT the
        // Child1 parameter; SIA001 fires only on the first argument.
        var source = """
            public class Base
            {
                public System.Guid Id { get; set; }
            }

            public class Child1 : Base;
            public class Child2 : Base;

            public class Holder
            {
                public static void Foo(System.Guid child1Id, System.Guid baseId) { }

                public void Use()
                {
                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Child2"));
        IsTrue(message.Contains("Child1"));
    }

    [Test]
    public void Convention_InheritedId_StaticReceiverIsBase_DoesNotCarryDerivedTag()
    {
        // When the static receiver type is Base, the access carries only "Base" — the
        // derived-type tags aren't inferred because the caller didn't express them.
        var source = """
            public class Base
            {
                public System.Guid Id { get; set; }
            }

            public class Child1 : Base;

            public class Holder
            {
                public static void TakeChild1(System.Guid child1Id) { }

                public void Use(Base b) => TakeChild1(b.Id);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void Convention_InheritedId_AbstractClassWithExplicitAttributes_UnionsTags()
    {
        // Abstract class Base + override properties with explicit [Id] at every level.
        // Override chain walks Child1.Id -> Base.Id and unions both tags, so child1.Id
        // carries {"Child1","Base"} and satisfies parameters tagged either way while
        // child2.Id correctly fails the "Child1" parameter.
        var source = """
            public abstract class Base
            {
                [Id("Base")]
                public abstract System.Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                [Id("Child1")]
                public override System.Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                [Id("Child2")]
                public override System.Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(System.Guid child1Id, System.Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        AreEqual(1, flow.Length);
        AreEqual("SIA001", flow[0].Id);
        var message = flow[0].GetMessage();
        IsTrue(message.Contains("Child2"));
        IsTrue(message.Contains("Child1"));
    }

    [Test]
    public void Convention_InheritedId_InterfaceWithExplicitAttributes_UnionsTags()
    {
        // Interface Base + class impls with explicit [Id] at every level. Implicit
        // interface implementation is walked, so child1.Id carries {"Child1","Base"}.
        var source = """
            public interface Base
            {
                [Id("Base")]
                System.Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                [Id("Child1")]
                public System.Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                [Id("Child2")]
                public System.Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(System.Guid child1Id, System.Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        AreEqual(1, flow.Length);
        AreEqual("SIA001", flow[0].Id);
        var message = flow[0].GetMessage();
        IsTrue(message.Contains("Child2"));
        IsTrue(message.Contains("Child1"));
    }

    [Test]
    public void Convention_InheritedId_InterfaceWithConventionOnly_UnionsTags()
    {
        // Interface Base with an abstract Id, children implement without explicit [Id].
        // Both convention tags (Child1/Child2 + Base) come from the interface walk.
        var source = """
            public interface Base
            {
                System.Guid Id { get; set; }
            }

            public class Child1 : Base
            {
                public System.Guid Id { get; set; }
            }

            public class Child2 : Base
            {
                public System.Guid Id { get; set; }
            }

            public class Holder
            {
                public static void Foo(System.Guid child1Id, System.Guid baseId) { }

                public void Use()
                {
                    var child1 = new Child1();
                    Foo(child1.Id, child1.Id);

                    var child2 = new Child2();
                    Foo(child2.Id, child2.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        AreEqual(1, flow.Length);
        AreEqual("SIA001", flow[0].Id);
        var message = flow[0].GetMessage();
        IsTrue(message.Contains("Child2"));
        IsTrue(message.Contains("Child1"));
    }

    [Test]
    public void Convention_InheritedId_DeepChain_IncludesAllAncestors()
    {
        // leaf.Id walks Leaf → Mid → Root and carries all three tags.
        var source = """
            public class Root { public System.Guid Id { get; set; } }
            public class Mid : Root;
            public class Leaf : Mid;

            public class Holder
            {
                public static void TakeRoot(System.Guid rootId) { }
                public static void TakeMid(System.Guid midId) { }
                public static void TakeLeaf(System.Guid leafId) { }

                public void Use(Leaf leaf)
                {
                    TakeRoot(leaf.Id);
                    TakeMid(leaf.Id);
                    TakeLeaf(leaf.Id);
                }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void Convention_ParameterCamelCase_InferredAsPascal()
    {
        // Parameter `orderId` (camelCase) -> "Order" tag; passing into a "Customer"-tagged
        // target fires SIA001 with "Order" vs "Customer".
        var source = """
            public class Holder
            {
                public System.Guid CustomerId { get; set; }

                public static void ProcessOrder(System.Guid orderId) { }

                public void Trigger() => ProcessOrder(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Order"));
        IsTrue(message.Contains("Customer"));
    }

    [Test]
    public void Convention_ParameterBareId_NoConvention()
    {
        // A parameter named just `id` has no containing-type equivalent and must NOT
        // receive an inferred tag — otherwise generic helpers would be over-tagged.
        var source = """
            public class Holder
            {
                public System.Guid CustomerId { get; set; }

                public static void Lookup(System.Guid id) { }

                public void Trigger() => Lookup(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA003", diagnostics[0].Id);
    }

    [Test]
    public void SIA005_RedundantAttributeOnParameter_Fires()
    {
        var source = """
            public class Holder
            {
                public static void Process([Id("Order")] System.Guid orderId) { }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia005 = diagnostics.Where(_ => _.Id == "SIA005").ToArray();

        AreEqual(1, sia005.Length);
        IsTrue(sia005[0].GetMessage().Contains("Order"));
    }

    [Test]
    public void SIA005_DifferentExplicitValue_NoDiagnostic()
    {
        var source = """
            public class Order
            {
                [Id("Special")]
                public System.Guid Id { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Count(_ => _.Id == "SIA005"));
    }

    [Test]
    public void Union_SourceUnion_TargetSingleInOverlap_NoDiagnostic()
    {
        // [UnionId("Customer","Product")] source overlaps with [Id("Customer")] target.
        var source = """
            public class Holder
            {
                [UnionId("Customer", "Product")]
                public System.Guid Value { get; set; }

                public static void Consume([Id("Customer")] System.Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void Union_SourceUnion_TargetSingleDisjoint_FiresSIA001()
    {
        var source = """
            public class Holder
            {
                [UnionId("Customer", "Product")]
                public System.Guid Value { get; set; }

                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Customer"));
        IsTrue(message.Contains("Product"));
        IsTrue(message.Contains("Order"));
    }

    [Test]
    public void Union_TargetUnion_SourceSingleInOverlap_NoDiagnostic()
    {
        var source = """
            public class Holder
            {
                [Id("Customer")]
                public System.Guid CustomerValue { get; set; }

                public static void Consume([UnionId("Customer", "Product")] System.Guid value) { }

                public void Use() => Consume(CustomerValue);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void Union_UnionAndUnionOverlap_NoDiagnostic()
    {
        var source = """
            public class Holder
            {
                [UnionId("Customer", "Product")]
                public System.Guid SourceValue { get; set; }

                public static void Consume([UnionId("Product", "Order")] System.Guid value) { }

                public void Use() => Consume(SourceValue);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void Union_UnionAndUnionDisjoint_FiresSIA001()
    {
        var source = """
            public class Holder
            {
                [UnionId("Customer", "Product")]
                public System.Guid SourceValue { get; set; }

                public static void Consume([UnionId("Order", "Supplier")] System.Guid value) { }

                public void Use() => Consume(SourceValue);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
    }

    [Test]
    public void Union_CoexistsWithConvention_UnionsTags()
    {
        // A property named `CustomerId` with explicit [UnionId("Order")] — convention
        // would give "Customer" and the explicit union adds "Order". Passing it to an
        // [Id("Order")] parameter must succeed because "Order" is in the set.
        var source = """
            public class Holder
            {
                [UnionId("Order")]
                public System.Guid CustomerId { get; set; }

                public static void Consume([Id("Order")] System.Guid value) { }

                public void Use() => Consume(CustomerId);
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var flow = diagnostics.Where(_ => _.Id is "SIA001" or "SIA002" or "SIA003").ToArray();

        AreEqual(0, flow.Length);
    }

    [Test]
    public void SIA006_SingletonUnion_Fires()
    {
        var source = """
            public class Holder
            {
                [UnionId("Customer")]
                public System.Guid Value { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();

        AreEqual(1, sia006.Length);
        IsTrue(sia006[0].GetMessage().Contains("Customer"));
    }

    [Test]
    public void SIA006_SingletonUnion_OnParameter_Fires()
    {
        var source = """
            public class Holder
            {
                public static void Consume([UnionId("Customer")] System.Guid value) { }
            }
            """;

        var diagnostics = GetDiagnostics(source);
        var sia006 = diagnostics.Where(_ => _.Id == "SIA006").ToArray();

        AreEqual(1, sia006.Length);
    }

    [Test]
    public void SIA006_MultiOptionUnion_NoDiagnostic()
    {
        var source = """
            public class Holder
            {
                [UnionId("Customer", "Product")]
                public System.Guid Value { get; set; }
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Count(_ => _.Id == "SIA006"));
    }

    [Test]
    public void AnonymousType_PropertyConvention_IsSkipped()
    {
        var source = """
            public class Holder
            {
                [Id("ProgramBillBase")]
                public System.Guid ProgramBillId { get; set; }

                public object Shape() => new { BillId = ProgramBillId };
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void RecordPrimaryCtorParameterAttribute_AppliesToGeneratedProperty()
    {
        var source =
            """
            public record Holder([Id("Order")] System.Guid Value);

            public class Consumer
            {
                public void Consume([Id("Order")] System.Guid value) { }

                public void Use(Holder holder) => Consume(holder.Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    [Test]
    public void RecordPrimaryCtorParameterAttribute_PropertyMismatchAgainstTarget()
    {
        var source =
            """
            public record Holder([Id("Customer")] System.Guid Value);

            public class Consumer
            {
                public void Consume([Id("Order")] System.Guid value) { }

                public void Use(Holder holder) => Consume(holder.Value);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(1, diagnostics.Length);
        AreEqual("SIA001", diagnostics[0].Id);
        var message = diagnostics[0].GetMessage();
        IsTrue(message.Contains("Customer"));
        IsTrue(message.Contains("Order"));
    }

    static ImmutableArray<Diagnostic> GetDiagnostics(string source) =>
        GetDiagnosticsWithOptions(source, new Dictionary<string, string>());

    static ImmutableArray<Diagnostic> GetDiagnosticsWithOptions(
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
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
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

    static CSharpCompilation BuildCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(_ => MetadataReference.CreateFromFile(_))
            .ToList();

        return CSharpCompilation.Create(
            "Tests",
            [syntaxTree],
            trustedAssemblies,
            new(OutputKind.DynamicallyLinkedLibrary));
    }
}
