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
