// MetadataReference.CreateFromFile opens every TPA assembly to read its metadata —
// doing it per-test dominates the suite's wall time. Load once at type-init and share.
static class TrustedReferences
{
    static readonly (string Path, MetadataReference Reference)[] cache =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(_ => (_, (MetadataReference)MetadataReference.CreateFromFile(_)))
            .ToArray();

    public static IReadOnlyList<MetadataReference> All { get; } =
        cache.Select(_ => _.Reference).ToArray();

    // For tests that need to exclude specific assemblies from TPA (e.g. the test
    // assembly itself when it would redefine types declared inside the compilation).
    public static IReadOnlyList<MetadataReference> Where(Func<string, bool> pathFilter) =>
        cache.Where(_ => pathFilter(_.Path)).Select(_ => _.Reference).ToArray();
}
