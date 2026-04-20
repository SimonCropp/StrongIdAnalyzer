readonly struct NamespacePattern(ImmutableArray<string> segments, bool isWildcard)
{
    public ImmutableArray<string> Segments { get; } = segments;
    public bool IsWildcard { get; } = isWildcard;
}
