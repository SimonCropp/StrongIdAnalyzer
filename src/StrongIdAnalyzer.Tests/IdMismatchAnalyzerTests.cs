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
        var source =
            """
            public class Holder
            {
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                [Id("Customer")]
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
                [Id("Order")]
                public System.Guid OrderId { get; set; }
            }

            public class Holder
            {
                [Id("Customer")]
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
                [Id("Order")]
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
                [Id("Order")]
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
                [Id("Order")]
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
                [Id("Order")]
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
                [Id("Customer")]
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
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                [Id("Customer")]
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
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                [Id("Customer")]
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
                [Id("Order")]
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

                [Id("Order")]
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
                [Id("Order")]
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

                [Id("Order")]
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
        // derived static type should see no tag — SIA002, not SIA001.
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
        AreEqual("SIA002", diagnostics[0].Id);
    }

    [Test]
    public void OverriddenMethodParameter_InheritsTagFromBase()
    {
        // Abstract base has [Id("Order")] on the parameter. Override drops the attribute.
        // Call through the derived receiver should still see SIA001.
        var source = """
            public abstract class Base
            {
                public abstract void Process([Id("Order")] System.Guid orderId);
            }

            public class Impl : Base
            {
                public override void Process(System.Guid orderId) { }
            }

            public class Consumer
            {
                [Id("Customer")]
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
                [Id("Order")]
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
                [Id("Order")]
                public System.Guid OrderId { get; set; }

                public System.Guid Use(Helper h) => h.Identity(OrderId);
            }
            """;

        var diagnostics = GetDiagnostics(source);

        AreEqual(0, diagnostics.Length);
    }

    static ImmutableArray<Diagnostic> GetDiagnostics(string source)
    {
        var compilation = BuildCompilation(source);

        // Run the generator so StrongIdAnalyzer.IdAttribute exists in the compilation.
        var driver = CSharpGeneratorDriver.Create(new IdAttributeGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var analyzer = new IdMismatchAnalyzer();

        return updated
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
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
