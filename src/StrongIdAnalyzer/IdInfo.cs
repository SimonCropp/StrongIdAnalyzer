// A value's Id info is a set of tags. Empty-with-state-Present is not allowed —
// use NotPresent instead. Multi-tag sets arise from receiver-type walking at
// access sites: `child1.Id` where `Id` is declared on Base carries both
// "Child1" and "Base", so it satisfies parameters tagged either way.

readonly struct IdInfo
{
    public IdState State { get; }
    public ImmutableArray<string> Tags { get; }

    // Subset of Tags that came from explicit [Id]/[UnionId] attributes (as opposed
    // to convention inference from member name or receiver type). When non-empty,
    // SIA002/SIA003 fixes propose only these tags — guessing convention names onto
    // the untagged side would override the deliberate annotation on the tagged side.
    public ImmutableArray<string> ExplicitTags { get; }

    IdInfo(IdState state, ImmutableArray<string> tags, ImmutableArray<string> explicitTags)
    {
        State = state;
        Tags = tags;
        ExplicitTags = explicitTags;
    }

    public static IdInfo Unknown { get; } = new(IdState.Unknown, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
    public static IdInfo NotPresent { get; } = new(IdState.NotPresent, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    public static IdInfo Present(string tag) =>
        new(IdState.Present, [tag], ImmutableArray<string>.Empty);

    public static IdInfo Present(ImmutableArray<string> tags) =>
        tags.IsDefaultOrEmpty
            ? NotPresent
            : new(IdState.Present, tags, ImmutableArray<string>.Empty);

    public static IdInfo Present(ImmutableArray<string> tags, ImmutableArray<string> explicitTags) =>
        tags.IsDefaultOrEmpty
            ? NotPresent
            : new(IdState.Present, tags, explicitTags.IsDefault ? ImmutableArray<string>.Empty : explicitTags);

    public static IdInfo PresentExplicit(ImmutableArray<string> tags) =>
        tags.IsDefaultOrEmpty
            ? NotPresent
            : new(IdState.Present, tags, tags);

    // Single-value accessor for the fixer (which needs one string to write back).
    // Picks the first tag — callers that care about multi-tag must use Tags directly.
    public string? FirstValue => Tags.IsDefaultOrEmpty ? null : Tags[0];

    // Set intersection — the source and target are compatible if they share at least
    // one tag. This is the natural rule for both covariant sources (receiver walk:
    // `child1.Id` carries {"Child1","Base"} so it matches a `[Id("Base")]` or
    // `[Id("Child1")]` parameter) and contravariant targets (`[UnionId("A","B")]`
    // accepts anything tagged "A" or "B").
    public bool IntersectsWith(IdInfo other)
    {
        foreach (var tag in Tags)
        {
            if (other.Tags.Contains(tag))
            {
                return true;
            }
        }
        return false;
    }

    // Flat representation for diagnostic messages. Multi-tag sets use "/" as a
    // separator so a reader sees the full set at once: [Id("Child1/Base")].
    public string Format() =>
        Tags.IsDefaultOrEmpty ? "" : string.Join("/", Tags);
}
