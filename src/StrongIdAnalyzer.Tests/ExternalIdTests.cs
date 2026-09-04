// `[assembly: ExternalId(typeof(T), nameof(T.Member), "Tag")]` — tagging a property or
// field the compilation does not own. The library in each test is emitted to metadata so
// the mapped member really is one the consumer cannot annotate.
public class ExternalIdTests
{
    // Microsoft.Graph.Models.User : DirectoryObject : Entity, `Id` on Entity — the shape
    // from JayBazuzi/StrongGuidExample. Group is a sibling of User for the chain tests.
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

            public class Group : DirectoryObject
            {
            }
        }
        """;

    const string mapDirectoryObject =
        """
        [assembly: ExternalId(typeof(Microsoft.Fake.DirectoryObject), nameof(Microsoft.Fake.DirectoryObject.Id), "EntraObject")]
        """;

    [Test]
    public async Task MappedBase_AssignmentToMatchingTarget_NoDiagnostic()
    {
        var consumer =
            mapDirectoryObject +
            """

            public class StrongGuid
            {
                public string _entraObjectId;

                public void G(Microsoft.Fake.User entraUser) =>
                    _entraObjectId = entraUser.Id;
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task MappedBase_AssignmentToOtherDomain_SIA001()
    {
        var consumer =
            mapDirectoryObject +
            """

            public class StrongGuid
            {
                public string _customerId;

                public void G(Microsoft.Fake.User entraUser) =>
                    _customerId = entraUser.Id;
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.GetMessage()).Contains("""property 'Entity.Id' is [Id("EntraObject")]""");
        // Slot 0 is the target (editable), slot 1 the source — metadata, so the fixer
        // gets the Location.None sentinel and offers nothing for that side.
        await Assert.That(diagnostic.AdditionalLocations[0].IsInSource).IsTrue();
        await Assert.That(diagnostic.AdditionalLocations[1].IsInSource).IsFalse();
    }

    [Test]
    public async Task MappedBase_Argument_SIA001()
    {
        var consumer =
            mapDirectoryObject +
            """

            public static class Sink
            {
                public static void Take(string customerId) { }

                public static void Run(Microsoft.Fake.Group group) => Take(group.Id);
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task ChainUnion_MostDerivedFirst()
    {
        // Two mappings on the chain: `user.Id` is {"EntraUser","EntraObject"} (derived
        // first, so the fixer's single value is the specific one); `group.Id` only
        // reaches the DirectoryObject mapping.
        var consumer =
            mapDirectoryObject +
            """

            [assembly: ExternalId(typeof(Microsoft.Fake.User), "Id", "EntraUser")]

            public class Holder
            {
                public string _entraUserId;
                public string _entraObjectId;
                public string _customerId;

                public void Ok(Microsoft.Fake.User user, Microsoft.Fake.Group group)
                {
                    _entraUserId = user.Id;
                    _entraObjectId = user.Id;
                    _entraObjectId = group.Id;
                }

                public void GroupIsNotUser(Microsoft.Fake.Group group) =>
                    _entraUserId = group.Id;

                public void UserIsNotCustomer(Microsoft.Fake.User user) =>
                    _customerId = user.Id;
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001"]);
        var messages = diagnostics.Select(_ => _.GetMessage()).ToArray();
        await Assert.That(messages.Count(_ => _.Contains("""is [Id("EntraObject")] and flows to field 'Holder._entraUserId'"""))).IsEqualTo(1);
        await Assert.That(messages.Count(_ => _.Contains("""is [Id("EntraUser/EntraObject")] and flows to field 'Holder._customerId'"""))).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatedAttribute_UnionsIds()
    {
        var consumer =
            mapDirectoryObject +
            """

            [assembly: ExternalId(typeof(Microsoft.Fake.DirectoryObject), "Id", "Principal")]

            public class Holder
            {
                public string _principalId;
                public string _entraObjectId;

                public void Ok(Microsoft.Fake.User user)
                {
                    _principalId = user.Id;
                    _entraObjectId = user.Id;
                }
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task MappedMemberAsTarget_SIA001()
    {
        // Writes through the mapping are checked too: assignment and object initializer.
        var consumer =
            mapDirectoryObject +
            """

            public class Holder
            {
                public string CustomerId { get; set; }

                public void Assign(Microsoft.Fake.User user) => user.Id = CustomerId;

                public Microsoft.Fake.User Build() => new Microsoft.Fake.User { Id = CustomerId };
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001"]);
        foreach (var diagnostic in diagnostics)
        {
            await Assert.That(diagnostic.GetMessage()).Contains("""which is [Id("EntraObject")]""");
        }
    }

    [Test]
    public async Task MappingOnInterface_AppliesToImplementations()
    {
        var library =
            """
            namespace Sdk
            {
                public interface IEntity
                {
                    string Id { get; }
                }

                public class Account : IEntity
                {
                    public string Id { get; set; }
                }
            }
            """;

        var consumer =
            """
            [assembly: ExternalId(typeof(Sdk.IEntity), nameof(Sdk.IEntity.Id), "Ledger")]

            public class Holder
            {
                public string _customerId;

                public void Run(Sdk.Account account) => _customerId = account.Id;
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""[Id("Ledger")]""");
    }

    [Test]
    public async Task External_BeatsLibraryAttribute()
    {
        var library =
            """
            using System;

            public class Vault
            {
                [Id("Vault")]
                public Guid Token { get; set; }
            }
            """;

        var consumer =
            """
            using System;

            [assembly: ExternalId(typeof(Vault), nameof(Vault.Token), "Secret")]

            public class Holder
            {
                public Guid SecretId { get; set; }
                public Guid VaultId { get; set; }

                public void Ok(Vault vault) => SecretId = vault.Token;

                public void Mismatch(Vault vault) => VaultId = vault.Token;
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""is [Id("Secret")] and flows to property 'Holder.VaultId'""");
    }

    [Test]
    public async Task External_BeatsShippedIndex()
    {
        // The library ships a [StrongIdIndex] resolving Vault.Token to "Vault"; the
        // consumer's mapping still wins.
        var library =
            """
            using System;

            [assembly: StrongIdAnalyzer.StrongIdIndexAttribute("P:Vault.Token=Vault")]

            namespace StrongIdAnalyzer
            {
                [AttributeUsage(AttributeTargets.Assembly)]
                internal sealed class StrongIdIndexAttribute(string encoded) : Attribute
                {
                    public string Encoded { get; } = encoded;
                }
            }

            public class Vault
            {
                public Guid Token { get; set; }
            }
            """;

        var consumer =
            """
            using System;

            [assembly: ExternalId(typeof(Vault), nameof(Vault.Token), "Secret")]

            public class Holder
            {
                public Guid VaultId { get; set; }

                public void Mismatch(Vault vault) => VaultId = vault.Token;
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""is [Id("Secret")]""");
    }

    [Test]
    public async Task MappingDeclaredInReferencedAssembly_IsHonoured()
    {
        // A shared project can carry the mapping for everyone that references it.
        var library =
            """
            using System;

            [assembly: ExternalId(typeof(Vault), nameof(Vault.Token), "Secret")]

            public class Vault
            {
                public Guid Token { get; set; }
            }
            """;

        var consumer =
            """
            using System;

            public class Holder
            {
                public Guid VaultId { get; set; }

                public void Mismatch(Vault vault) => VaultId = vault.Token;
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""is [Id("Secret")]""");
    }

    [Test]
    public async Task ExternalTag_IsKnownForSuffixInference()
    {
        // "EntraObject" exists only through the mapping; with suffix inference on,
        // `sourceEntraObjectId` resolves to it instead of to "SourceEntraObject".
        var consumer =
            mapDirectoryObject +
            """

            public class Holder
            {
                [Id("Customer")]
                public string Value { get; set; }

                public void Take(string sourceEntraObjectId) { }

                public void Run() => Take(Value);
            }
            """;

        var diagnostics = await Analyze(
            graphLibrary,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.infer_suffix_ids"] = "true"
            });

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""which is [Id("EntraObject")]""");
    }

    [Test]
    public async Task SuppressedAssembly_MappingStillApplies()
    {
        // Suppression withholds convention tags; a mapping is the user's explicit word and
        // is consulted before suppression is even considered.
        var consumer =
            mapDirectoryObject +
            """

            public class Holder
            {
                public string _customerId;

                public void Run(Microsoft.Fake.User user) => _customerId = user.Id;
            }
            """;

        var diagnostics = await Analyze(
            graphLibrary,
            consumer,
            new Dictionary<string, string>
            {
                ["strongidanalyzer.suppressed_assemblies"] = "Messages"
            });

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task MissingMember_SIA008()
    {
        var consumer =
            """
            [assembly: ExternalId(typeof(Microsoft.Fake.User), "Idd", "EntraObject")]

            public class Holder
            {
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA008"]);
        await Assert.That(diagnostics[0].Location.IsInSource).IsTrue();
        await Assert.That(diagnostics[0].Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyIds_SIA008()
    {
        var consumer =
            """
            [assembly: ExternalId(typeof(Microsoft.Fake.User), "Id")]
            [assembly: ExternalId(typeof(Microsoft.Fake.User), "Id", "")]
            [assembly: ExternalId(typeof(Microsoft.Fake.User), "Id", " ")]

            public class Holder
            {
            }
            """;

        var diagnostics = await Analyze(graphLibrary, consumer);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA008", "SIA008", "SIA008"]);
    }

    [Test]
    public async Task MissingMemberInReferencedAssemblyAttribute_NotReported()
    {
        // Only the compilation's own attributes are validated — nothing here is editable.
        var library =
            """
            using System;

            [assembly: ExternalId(typeof(Vault), "Nope", "Secret")]

            public class Vault
            {
                public Guid Token { get; set; }
            }
            """;

        var consumer =
            """
            public class Holder
            {
            }
            """;

        var diagnostics = await Analyze(library, consumer);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task BclMember_SameCompilation()
    {
        // No library needed: the mapped member is Process.Id from the framework, which
        // the default namespace suppression would otherwise leave untagged.
        var source =
            """
            using System.Diagnostics;

            [assembly: ExternalId(typeof(Process), nameof(Process.Id), "Process")]

            public class Holder
            {
                public int ProcessId { get; set; }
                public int JobId { get; set; }

                public void Ok(Process process) => ProcessId = process.Id;

                public void Mismatch(Process process) => JobId = process.Id;
            }
            """;

        var diagnostics = await Analyze(null, source);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage()).Contains("""property 'Process.Id' is [Id("Process")]""");
    }

    // Compiles `library` (null: none) into an in-memory assembly named `Messages`,
    // references it from `consumer`, and returns the analyzer's diagnostics on the
    // consumer only.
    static Task<ImmutableArray<Diagnostic>> Analyze(
        string? library,
        string consumer,
        IDictionary<string, string>? options = null)
    {
        var references = new List<MetadataReference>(TrustedReferences.All);
        if (library is not null)
        {
            var libraryCompilation = Compile("Messages", library, references);
            using var stream = new MemoryStream();
            var emit = libraryCompilation.Emit(stream);
            if (!emit.Success)
            {
                var errors = string.Join("\n", emit.Diagnostics.Where(_ => _.Severity == DiagnosticSeverity.Error));
                throw new($"Library compilation failed:\n{errors}");
            }

            stream.Position = 0;
            references.Add(MetadataReference.CreateFromStream(stream));
        }

        var consumerCompilation = Compile("Consumer", consumer, references);
        var analyzerOptions = new AnalyzerOptions(
            [],
            new TestAnalyzerConfigOptionsProvider(options ?? new Dictionary<string, string>()));

        return consumerCompilation
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
