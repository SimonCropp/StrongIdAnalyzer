

// The Roslyn version an analyzer is compiled against is a shipping contract: any compiler
// older than it skips the assembly with CS9057. That is a warning, not an error, so the
// symptom is every diagnostic silently disappearing — and IdAttributeGenerator goes with
// it, so consumer code tagged with [Id] / [UnionId] fails with CS0246 as well.
//
// Bumping Microsoft.CodeAnalysis.CSharp in Directory.Packages.props therefore drops every
// toolchain below the new version, which is a release note rather than a routine update.
// Nothing else catches it: the build is green either way, and the unit and integration
// suites both run on the newest SDK. Pinning the version baked into the built assemblies
// means this fails here rather than on a consumer's machine.
public class RoslynFloorTests
{
    // Roslyn 4.11 is the .NET 8 SDK's compiler. It is also the practical lower bound —
    // 4.8 and below bundle a System.Collections.Immutable without CollectionBuilder
    // support, where this repo's collection expressions fail to compile (CS9210).
    const string floor = "4.11.0.0";

    [Test]
    public Task AnalyzerIsBuiltAgainstTheFloor() =>
        AssertRoslynReference(typeof(IdMismatchAnalyzer).Assembly);

    // A separate assembly, packed into the same analyzers/dotnet/cs folder, so it is
    // subject to the same load check.
    [Test]
    public Task CodeFixesAreBuiltAgainstTheFloor() =>
        AssertRoslynReference(typeof(AddIdCodeFixProvider).Assembly);

    static async Task AssertRoslynReference(Assembly assembly)
    {
        // Every Microsoft.CodeAnalysis.* reference is checked, not just the core one: the
        // code fixes also bind Workspaces, and the compiler's load check looks at all of
        // them. Asserting on the rendered text rather than a count so a failure names the
        // assembly and version instead of reading "expected 0 but found 2".
        var versions = assembly
            .GetReferencedAssemblies()
            .Where(_ => _.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true)
            .Select(_ => $"{_.Name} {_.Version}")
            .Where(_ => !_.EndsWith(floor, StringComparison.Ordinal))
            .OrderBy(_ => _);

        await Assert.That(string.Join(", ", versions)).IsEqualTo("");
    }
}
