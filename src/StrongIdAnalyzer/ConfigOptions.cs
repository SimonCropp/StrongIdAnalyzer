// Reads an .editorconfig key for one syntax tree: the tree's own options (`[*.cs]`
// sections, with any .globalconfig already merged in by the compiler), falling back to
// GlobalOptions when there is no tree. Null means the key is not set.
//
// Options are per tree, not per compilation: a compilation can contain files outside the
// project's .editorconfig scope (TUnit 1.71+ injects TUnit.Core.GeneratedNamespace.cs from
// the NuGet cache), and different folders can set different values. Configs maps each
// tree to the Config built for its option values.
static class ConfigOptions
{
    public static string? Get(
        AnalyzerConfigOptionsProvider options,
        SyntaxTree? tree,
        string key)
    {
        if (tree is not null &&
            options.GetOptions(tree).TryGetValue(key, out var value))
        {
            return value;
        }

        if (options.GlobalOptions.TryGetValue(key, out var global))
        {
            return global;
        }

        return null;
    }
}
