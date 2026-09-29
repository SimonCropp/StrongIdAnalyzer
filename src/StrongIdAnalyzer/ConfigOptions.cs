// Reads a project-level .editorconfig key. `[*.cs]` entries are per-tree and never surface
// via GlobalOptions, so the trees are sampled — but not just the first one: a compilation
// can open with a file outside the project's .editorconfig scope (TUnit 1.71+ injects
// TUnit.Core.GeneratedNamespace.cs from the NuGet cache ahead of the project's own files),
// and that tree carries none of the keys. The first tree that has the key wins; the value
// is project-uniform in practice. GlobalOptions (.globalconfig) is the fallback.
static class ConfigOptions
{
    public static bool TryGetValue(
        AnalyzerConfigOptionsProvider options,
        Compilation compilation,
        string key,
        out string value)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (options.GetOptions(tree).TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
        }

        if (options.GlobalOptions.TryGetValue(key, out var global))
        {
            value = global;
            return true;
        }

        value = "";
        return false;
    }
}
