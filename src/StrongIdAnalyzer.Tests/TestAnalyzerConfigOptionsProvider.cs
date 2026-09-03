// Feeds .editorconfig-style options to the analyzer under test. The same dictionary is
// returned for global options and for every tree, which matches how the analyzer reads
// its keys (from the first syntax tree's options).
sealed class TestAnalyzerConfigOptions(IDictionary<string, string> options)
    : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        if (options.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = null!;
        return false;
    }
}

sealed class TestAnalyzerConfigOptionsProvider(IDictionary<string, string> options)
    : AnalyzerConfigOptionsProvider
{
    readonly TestAnalyzerConfigOptions globals = new(options);

    public override AnalyzerConfigOptions GlobalOptions =>
        globals;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        globals;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        globals;
}
