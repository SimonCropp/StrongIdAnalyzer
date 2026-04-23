// Opt-in inference of tags on members (properties, fields, parameters) whose name ends
// in an `XxxId` suffix where `Xxx` matches a known tag in the compilation — e.g.
// `sourceProductId` / `SourceProductId` -> "Product" when a `Product` type with a
// conventional `Id` member exists. Gated by .editorconfig key
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

    // Matches `[prefix][Word]Id` where `Word` is the last upper-case boundary before the
    // trailing `Id`. Returns the word only if it is (ordinally) a known tag. The
    // whole-name `XxxId` case (no prefix) is handled by the existing convention rule and
    // is rejected here to avoid duplicate tagging paths.
    public static bool TryMatch(string name, ImmutableHashSet<string> knownTags, out string tag)
    {
        tag = "";
        if (name.Length <= 2 || !name.EndsWith("Id", StringComparison.Ordinal))
        {
            return false;
        }

        var prefixLength = name.Length - 2;
        var wordStart = prefixLength - 1;
        while (wordStart > 0 && !char.IsUpper(name[wordStart]))
        {
            wordStart--;
        }

        // wordStart == 0 means the entire name is `XxxId` (no prefix before the last word).
        // That's the existing `TryGetConventionName` rule's territory — don't double-handle.
        if (wordStart == 0)
        {
            return false;
        }

        var word = name.Substring(wordStart, prefixLength - wordStart);
        if (knownTags.Contains(word))
        {
            tag = word;
            return true;
        }

        return false;
    }
}
