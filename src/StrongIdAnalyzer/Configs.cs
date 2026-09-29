// One Config per distinct set of .editorconfig values in the compilation, and the tree →
// Config map that picks one for each analysis action. Trees sharing the same values share
// a Config, so a project with one .editorconfig builds exactly one — and its caches (known
// tags, type-name map, wrapper recognition) are shared as before. A tree the project's
// .editorconfig does not cover gets the defaults without affecting any other tree.
//
// ExternalIds is option-independent and shared by every Config.
sealed class Configs(
    AnalyzerConfigOptionsProvider options,
    Compilation compilation,
    ExternalIds externalIds)
{
    ConcurrentDictionary<SyntaxTree, Config> perTree = new();
    ConcurrentDictionary<(string? Namespaces, string? Assemblies, string? Suffix, string? Wrappers), Config> perValues = new();

    public Config For(SyntaxTree? tree)
    {
        if (tree is null)
        {
            return ForValues(Read(null));
        }

        return perTree.GetOrAdd(tree, _ => ForValues(Read(_)));
    }

    public Config For(ISymbol symbol) =>
        For(symbol.Locations.FirstOrDefault()?.SourceTree);

    (string?, string?, string?, string?) Read(SyntaxTree? tree) =>
        (
            ConfigOptions.Get(options, tree, Suppression.NamespacesKey),
            ConfigOptions.Get(options, tree, Suppression.AssembliesKey),
            ConfigOptions.Get(options, tree, SuffixInference.OptionKey),
            ConfigOptions.Get(options, tree, WrapperTypes.OptionKey)
        );

    Config ForValues((string? Namespaces, string? Assemblies, string? Suffix, string? Wrappers) values) =>
        perValues.GetOrAdd(values, Create);

    Config Create((string? Namespaces, string? Assemblies, string? Suffix, string? Wrappers) values)
    {
        var suppression = Suppression.Create(values.Namespaces, values.Assemblies);
        // Built before Config because the known-tags computation consults them too.
        var wrappers = new WrapperTypes(WrapperTypes.Parse(values.Wrappers), suppression);
        return new(
            suppression,
            SuffixInference.Parse(values.Suffix),
            wrappers,
            externalIds,
            compilation,
            new(() => StrongIdAnalyzer.IdMismatchAnalyzer.CollectKnownTags(compilation, suppression, wrappers, externalIds)));
    }
}
