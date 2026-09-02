// Every tag observed in the source compilation, kept split by where it came from.
// `All` is the flat set suffix inference matches against. The split exists for SIA005,
// which asks a different question — "would this tag still be known if this declaration's
// own attribute were deleted?" — because an explicit attribute contributes its own tag to
// the set. Without the distinction `[Id("SourceProduct")] Guid sourceProductId` matches
// the very candidate it is supplying, gets reported as redundant, and the fix that
// removes it silently retags the member to "Product".
sealed class TagIndex(
    ImmutableHashSet<string> all,
    ImmutableHashSet<string> typeTags,
    Dictionary<string, int> explicitCounts)
{
    public ImmutableHashSet<string> All { get; } = all;

    // The set to match against when asking what a declaration's tag would be with its own
    // `[Id(tag)]` removed. The tag survives when something else vouches for it: a type
    // with an `Id` member, or a second declaration spelling the same tag out. Callers pass
    // a tag their own symbol contributes, so a lone explicit contribution is that symbol's.
    public ImmutableHashSet<string> Without(string tag)
    {
        if (typeTags.Contains(tag) ||
            (explicitCounts.TryGetValue(tag, out var count) && count > 1))
        {
            return All;
        }

        return All.Remove(tag);
    }
}
