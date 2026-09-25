// `strongidanalyzer.suppressed_namespaces` and `strongidanalyzer.suppressed_assemblies`
// share one predicate (Suppression.IsSuppressed). These tests pin the two behaviours that
// depend on it beyond the SIA002/SIA003 fix-site checks already covered elsewhere:
//
//  * the receiver-type walk at an access site (`user.Id`) contributes a type's name as a
//    convention tag only when that type is outside both lists — a referenced domain
//    assembly keeps working, an SDK such as Microsoft.Graph stops inventing tags the
//    user cannot fix;
//  * the assembly list reaches every consumer of the predicate: fix-site suppression,
//    wrapper recognition, and the KnownTags walk behind suffix inference.
//
// Every library here is emitted to metadata under a chosen assembly name, so both the
// "declared in a referenced assembly" shape and the assembly-name patterns are real.
public class SuppressionTests
{
    // Microsoft.Graph.Models.User : DirectoryObject : Entity, with `Id` on Entity. Before
    // the gate `entraUser.Id` read as {"User","DirectoryObject"} and the assignment into a
    // field tagged "EntraObject" was a false SIA001 (JayBazuzi/StrongGuidExample).
    const string graphLibrary =
        """
        namespace Microsoft.Fake
        {
            public class Entity
            {
                public string Id { get; set; }
            }

            public class DirectoryObject : Entity
            {
            }

            public class User : DirectoryObject
            {
            }
        }
        """;

    const string graphConsumer =
        """
        public class StrongGuid
        {
            public string _entraObjectId;

            public void G(Microsoft.Fake.User entraUser) =>
                _entraObjectId = entraUser.Id;
        }
        """;

    // Same shape outside the default lists: a domain assembly of the user's own solution.
    const string shopLibrary =
        """
        using System;

        namespace Shop
        {
            public class Entity
            {
                public Guid Id { get; set; }
            }

            public class Product : Entity
            {
            }
        }
        """;

    const string shopConsumer =
        """
        using System;

        public class Holder
        {
            public Guid OrderId { get; set; }

            public void G(Shop.Product product) =>
                OrderId = product.Id;
        }
        """;

    [Test]
    public async Task ReceiverWalk_MetadataTypeInSuppressedNamespace_NoConventionTag()
    {
        var diagnostics = await Analyze(graphLibrary, graphConsumer);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ReceiverWalk_MetadataTypeOutsideSuppressedNamespace_StillTagged()
    {
        var diagnostics = await Analyze(shopLibrary, shopConsumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("Fix: apply [Id<Product>]");
    }

    [Test]
    public async Task ReceiverWalk_NamespaceSuppressionDisabled_MetadataTypeTagged()
    {
        var diagnostics = await Analyze(
            graphLibrary,
            graphConsumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = ""
            });

        // `Entity` is in the set because it is where `Id` is declared, and a metadata
        // level contributes no tag of its own — so the receiver walk names it, exactly as
        // it names the derived levels. A source-declared `Entity` reaches the same set
        // through the member chain's naming rule instead; the two shapes agree.
        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage())
            .Contains("""[UnionId("User", "DirectoryObject", "Entity")]""");
    }

    [Test]
    public async Task ReceiverWalk_CustomNamespacePattern_SuppressesThirdPartySdk()
    {
        var library = shopLibrary.Replace("namespace Shop", "namespace Fake.Sdk");
        var consumer = shopConsumer.Replace("Shop.Product", "Fake.Sdk.Product");

        var diagnostics = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "System*,Microsoft*,Fake*"
            });

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ReceiverWalk_SuppressedAssembly_Exact()
    {
        var suppressed = await Analyze(
            shopLibrary,
            shopConsumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_assemblies"] = "Messages"
            });
        await Assert.That(suppressed).IsEmpty();

        // Without `*` the pattern is the whole name, not a prefix.
        var notSuppressed = await Analyze(
            shopLibrary,
            shopConsumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_assemblies"] = "Message"
            });
        await Assert.That(notSuppressed.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task ReceiverWalk_SuppressedAssembly_Wildcard()
    {
        var options = new Dictionary<string, string>
        {
            ["strongidanalyzer.suppressed_assemblies"] = "Messages.Graph*"
        };

        // Prefix covers the assembly itself and dotted children.
        await Assert.That(await Analyze(shopLibrary, shopConsumer, options, "Messages.Graph")).IsEmpty();
        await Assert.That(await Analyze(shopLibrary, shopConsumer, options, "Messages.Graph.Core")).IsEmpty();

        // Segment-wise, not a string prefix: `Messages.Graph*` does not cover
        // `Messages.Graphics`.
        var graphics = await Analyze(shopLibrary, shopConsumer, options, "Messages.Graphics");
        await Assert.That(graphics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task ReceiverWalk_SuppressedAssembly_BareWildcardMatchesEverything()
    {
        var diagnostics = await Analyze(
            shopLibrary,
            shopConsumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_assemblies"] = "*"
            });

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task SuppressedAssembly_TargetInReferencedAssembly_NoSIA003()
    {
        // Mirrors SuppressedNamespace_DefaultMicrosoft_NoSIA003 for the assembly list. The
        // untagged parameter lives in metadata, so SIA003 is already suppressed there;
        // the namespace list is emptied so only the assembly list can be doing the work.
        var library =
            """
            using System;

            namespace Sdk
            {
                public class Target
                {
                    public void Consume(Guid value) { }
                }
            }
            """;

        var consumer =
            """
            using System;

            namespace Sdk.Consumer
            {
                public class Consumer
                {
                    public Guid OrderId { get; set; }

                    public void Use(Sdk.Target target) => target.Consume(OrderId);
                }
            }
            """;

        var diagnostics = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "",
                ["strongidanalyzer.suppressed_assemblies"] = "Messages"
            });

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task SuppressedAssembly_SourceAssembly_NotSpecialCased()
    {
        // Same as the namespace list: a pattern that matches the compilation's own
        // assembly suppresses its declarations too. The consumer compiles as `Consumer`.
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

        var fires = await Analyze(shopLibrary, source);
        await Assert.That(fires.Select(_ => _.Id)).IsEquivalentTo(["SIA003"]);

        var suppressed = await Analyze(
            shopLibrary,
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_assemblies"] = "Consumer"
            });
        await Assert.That(suppressed).IsEmpty();
    }

    [Test]
    public async Task SuppressedAssembly_WrapperNotRecognised()
    {
        // With wrapper inference on, `holder.User.Value` carries "User" and flowing it into
        // an [Id("Order")] parameter is SIA001. Suppressing the wrapper's assembly drops
        // recognition: `.Value` is then an untagged metadata member and SIA002 is already
        // silent for metadata sources.
        var library =
            """
            using System;

            namespace Wrappers
            {
                public readonly struct UserId
                {
                    public UserId(Guid value) => Value = value;
                    public Guid Value { get; }
                }
            }
            """;

        var consumer =
            """
            using System;

            public class Holder
            {
                public Wrappers.UserId User { get; set; }
            }

            public static class Sink
            {
                public static void Take([Id("Order")] Guid value) { }

                public static void Run(Holder holder) => Take(holder.User.Value);
            }
            """;

        var recognised = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_wrapper_ids"] = "true"
            });
        await Assert.That(recognised.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);

        var suppressed = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_wrapper_ids"] = "true",
                ["strongidanalyzer.suppressed_assemblies"] = "Messages"
            });
        await Assert.That(suppressed).IsEmpty();
    }

    [Test]
    public async Task SuppressedAssembly_TypesDoNotSeedKnownTags()
    {
        // Suffix inference accepts "Group" for `sourceGroupId` only while some type named
        // Group with an Id member is known. That type lives in the referenced assembly;
        // suppressing the assembly removes it from the KnownTags walk and the parameter
        // falls back to the whole-name rule ("SourceGroup").
        var library =
            """
            using System;

            namespace Upstream
            {
                public class Group
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var consumer =
            """
            using System;

            public class Holder
            {
                [Id("Access")]
                public Guid Value { get; set; }

                public void Take(Guid sourceGroupId) { }

                public void Run() => Take(Value);
            }
            """;

        var known = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });
        await Assert.That(known.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(known[0].GetMessage()).Contains("""[Id("Group")]""");

        var suppressed = await Analyze(
            library,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true",
                ["strongidanalyzer.suppressed_assemblies"] = "Upstream*,Messages"
            });
        await Assert.That(suppressed.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(suppressed[0].GetMessage()).Contains("""[Id("SourceGroup")]""");
    }

    // A referenced domain type that declares `Id` DIRECTLY. The member-chain walk skips
    // metadata levels, so the level contributes nothing — and marking its type covered
    // anyway left the receiver walk nothing to add either, so `product.Id` came back
    // untagged. Only the `Product : Entity` shape was ever tested, and that one worked
    // because the receiver and the declaring type differ.
    [Test]
    public async Task ReceiverWalk_MetadataTypeDeclaringIdDirectly_StillTagged()
    {
        var library =
            """
            using System;

            namespace Shop
            {
                public class Product
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var consumer =
            """
            using System;

            public class Holder
            {
                public void Use(Shop.Product product) => UseOrder(product.Id);

                static void UseOrder([Id("Order")] Guid value) { }
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("[Id<Product>]");
    }

    // Ancestor widening searched every type in every referenced assembly by simple name,
    // suppression included. A domain class named `Process` therefore inherited
    // `System.Diagnostics.Process`'s base chain and silently accepted a "Component" id;
    // `Microsoft.Graph`'s `User : DirectoryObject : Entity` does the same to any domain
    // `User`. The predicate that decides "not the user's domain" everywhere else decides
    // it here too.
    [Test]
    public async Task Widening_IgnoresSuppressedNamespaces()
    {
        var source =
            """
            using System;

            public class Process
            {
                public Guid Id { get; set; }
            }

            public class Component
            {
                public Guid Id { get; set; }
            }

            public class Holder
            {
                public void Use(Process process) => UseComponent(process.Id);

                static void UseComponent([Id("Component")] Guid value) { }
            }
            """;

        var diagnostics = await Analyze(library: "public class Unused { }", source);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    // A domain base type in the source compilation still widens: the rule is suppression,
    // not "declared elsewhere".
    [Test]
    public async Task Widening_KeepsDomainAncestors()
    {
        var source =
            """
            using System;

            public class Entity
            {
                public Guid Key { get; set; }
            }

            public class Invoice : Entity
            {
            }

            public class Holder
            {
                [Id("Invoice")]
                public Guid Source { get; set; }

                public void Use() => TakeEntity(Source);

                static void TakeEntity([Id("Entity")] Guid value) { }
            }
            """;

        var diagnostics = await Analyze(library: "public class Unused { }", source);

        await Assert.That(diagnostics).IsEmpty();
    }

    // `Ext.*` is how everyone writes it. Splitting on `.` left an empty trailing segment
    // that no namespace chain can match, so the list quietly covered nothing.
    [Test]
    public async Task NamespacePattern_WithDotStar_MatchesLikeStar()
    {
        // One compilation: the fix site has to be editable for SIA003 to fire at all,
        // which is what makes the suppression verdict observable here.
        const string unused = "public class Unused { }";
        var source =
            """
            using System;

            namespace Ext.Vendor
            {
                public class Widget
                {
                    public Guid Slot { get; set; }
                }
            }

            public class Holder
            {
                [Id("Order")]
                public Guid Source { get; set; }

                public void Use(Ext.Vendor.Widget widget) => widget.Slot = Source;
            }
            """;

        var unsuppressed = await Analyze(unused, source);
        await Assert.That(unsuppressed.Select(_ => _.Id)).IsEquivalentTo(["SIA003"]);

        var dotStar = await Analyze(
            unused,
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "Ext.*"
            });
        await Assert.That(dotStar).IsEmpty();

        var star = await Analyze(
            unused,
            source,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_namespaces"] = "Ext*"
            });
        await Assert.That(star).IsEmpty();
    }

    // Compiles `library` (with the generator, so it can use [Id]) into an in-memory
    // assembly named `libraryAssemblyName`, references it from `consumer`, and returns the
    // analyzer's diagnostics on the consumer only.
    static Task<ImmutableArray<Diagnostic>> Analyze(
        string library,
        string consumer,
        IDictionary<string, string>? options = null,
        string libraryAssemblyName = "Messages")
    {
        var libraryCompilation = Compile(libraryAssemblyName, library, TrustedReferences.All);

        using var stream = new MemoryStream();
        var emit = libraryCompilation.Emit(stream);
        if (!emit.Success)
        {
            var errors = string.Join("\n", emit.Diagnostics.Where(_ => _.Severity == DiagnosticSeverity.Error));
            throw new($"Library compilation failed:\n{errors}");
        }

        stream.Position = 0;
        var libraryReference = MetadataReference.CreateFromStream(stream);

        var consumerCompilation = Compile("Consumer", consumer, [.. TrustedReferences.All, libraryReference]);
        var analyzerOptions = new AnalyzerOptions(
            [],
            new TestAnalyzerConfigOptionsProvider(options ?? new Dictionary<string, string>()));

        return consumerCompilation
            .SuppressStringTagHint()
            .WithAnalyzers([new IdMismatchAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    static Compilation Compile(string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver
            .Create(new IdAttributeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new($"{name} compilation has errors:\n{string.Join("\n", errors.Select(_ => _.ToString()))}");
        }

        return updated;
    }
}
