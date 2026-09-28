// SIA009 flags every `[Id("X")]` whose X names a type in scope, and SIA010 every
// `[UnionId("X", "Y")]` whose tags all do — which is most of the snippets in this suite,
// written before the rules existed and asserting exact diagnostic sets. The harnesses
// switch both off; StringTagNamesTypeTests turns them back on.
static class StringTagHint
{
    public static Compilation SuppressStringTagHint(this Compilation compilation) =>
        compilation.WithOptions(
            compilation.Options.WithSpecificDiagnosticOptions(
                compilation.Options.SpecificDiagnosticOptions
                    .SetItem("SIA009", ReportDiagnostic.Suppress)
                    .SetItem("SIA010", ReportDiagnostic.Suppress)));
}
