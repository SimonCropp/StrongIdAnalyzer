// Opt-in inference of tags on members (properties, fields, parameters) whose name ends
// in an `XxxId` suffix where `Xxx` (or some upper-case-delimited tail of `Xxx`) matches
// a known tag in the compilation — e.g. `sourceProductId` -> "Product" when a `Product`
// type with a conventional `Id` member exists. Gated by .editorconfig key
// `strongidanalyzer.infer_suffix_ids` (default false) — without the known-tag
// constraint this rule would over-tag common names like `hashedId`, `rawId`, `validId`.
static class SuffixInference
{
    const string optionKey = "strongidanalyzer.infer_suffix_ids";

    public static bool Read(
        AnalyzerConfigOptionsProvider options,
        Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.FirstOrDefault();
        if (tree is null || !options.GetOptions(tree).TryGetValue(optionKey, out var raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var value) && value;
    }

    // Matches `[prefix][Word]Id` where `Word` is a suffix starting at an upper-case
    // boundary before the trailing `Id`. Walks longest-first: the whole prefix is
    // checked first, then each shorter upper-case-delimited tail. The first candidate
    // that matches a known tag wins — so `AccessGroupId` with both `AccessGroup` and
    // `Group` known resolves to `"AccessGroup"` (whole-prefix exact match), while
    // `productOrderId` with only `Product` and `Order` known still resolves to
    // `"Order"` because `ProductOrder` isn't known. If no candidate matches, returns
    // false and the caller falls through to the existing whole-name convention rule.
    public static bool TryMatch(string name, ImmutableHashSet<string> knownTags, out string tag)
    {
        tag = "";
        if (name.Length <= 2 || !name.EndsWith("Id", StringComparison.Ordinal))
        {
            return false;
        }

        var prefixLength = name.Length - 2;

        for (var wordStart = 0; wordStart < prefixLength; wordStart++)
        {
            // Inner candidates must begin at an upper-case boundary. The whole-prefix
            // candidate (wordStart == 0) is always tried first regardless of casing —
            // a leading lower-case letter is normalised to upper-case so `accessGroupId`
            // matches `AccessGroup` the same as `AccessGroupId`.
            if (wordStart > 0 && !char.IsUpper(name[wordStart]))
            {
                continue;
            }

            var word = name.Substring(wordStart, prefixLength - wordStart);
            if (wordStart == 0 && char.IsLower(word[0]))
            {
                word = char.ToUpperInvariant(word[0]) + word.Substring(1);
            }

            if (knownTags.Contains(word))
            {
                tag = word;
                return true;
            }
        }

        return false;
    }
}
