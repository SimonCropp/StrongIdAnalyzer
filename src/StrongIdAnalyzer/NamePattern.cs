// One entry of a `strongidanalyzer.suppressed_namespaces` / `suppressed_assemblies`
// list, pre-split on `.` so matching compares segments without allocating. A trailing
// `*` in the .editorconfig value sets IsWildcard: the pattern then matches the prefix
// itself and anything nested under it (`System*` matches `System` and `System.IO`, but
// not `SystemX`).
readonly struct NamePattern(ImmutableArray<string> segments, bool isWildcard)
{
    public ImmutableArray<string> Segments { get; } = segments;
    public bool IsWildcard { get; } = isWildcard;
}
