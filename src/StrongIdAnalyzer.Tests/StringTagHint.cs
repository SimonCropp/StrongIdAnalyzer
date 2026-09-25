// SIA009 flags every `[Id("X")]` whose X names a type in scope — which is most of the
// snippets in this suite, written before the rule existed and asserting exact diagnostic
// sets. The harnesses switch it off; StringTagNamesTypeTests turns it back on.
static class StringTagHint
{
    public static Compilation SuppressStringTagHint(this Compilation compilation) =>
        compilation.WithOptions(
            compilation.Options.WithSpecificDiagnosticOptions(
                compilation.Options.SpecificDiagnosticOptions.SetItem("SIA009", ReportDiagnostic.Suppress)));
}
