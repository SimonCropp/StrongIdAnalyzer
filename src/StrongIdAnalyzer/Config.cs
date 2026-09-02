// Per-compilation analysis state: the resolved .editorconfig options plus the caches
// that let repeated tag lookups reuse work across every operation in the compilation.
// Constructed once per CompilationStartAction and passed by value to each analysis
// entry point — the reference-typed cache fields are shared, so copies stay coherent.
//
// Deliberately knows nothing about tag resolution itself. `knownTags` arrives as an
// already-built Lazy so the computation stays with the analyzer that understands what
// a tag is, while this type only owns when it runs and who shares the result.
readonly struct Config(
    ImmutableArray<NamespacePattern> suppressedNamespaces,
    bool inferSuffixTags,
    Compilation compilation,
    Lazy<TagIndex> knownTags)
{
    public ImmutableArray<NamespacePattern> SuppressedNamespaces { get; } = suppressedNamespaces;
    public bool InferSuffixTags { get; } = inferSuffixTags;
    public Compilation Compilation { get; } = compilation;

    // Every tag observed in the source compilation — convention-derived and explicit.
    // Computed on the first suffix-inference attempt and shared for the rest of the
    // compilation. Thread-safe via Lazy<T>'s default publication mode.
    public Lazy<TagIndex> KnownTags { get; } = knownTags;

    // Tag-to-ancestor-name cache. Keyed by a tag string; value is the union of base-type
    // and interface names for every type in the compilation whose simple name equals
    // the tag. Computed lazily and shared across all comparisons in the same compilation.
    public ConcurrentDictionary<string, ImmutableArray<string>> AncestorTagCache { get; } =
        new(StringComparer.Ordinal);

    // Per-assembly tag index loaded lazily from [assembly: StrongIdIndex(...)].
    // When a referenced assembly ships an index, per-symbol tag lookups skip the
    // inheritance walk entirely — a hit returns the pre-resolved tag set directly.
    // Null entries mean "this assembly has no index, fall back to the walk".
    public ConcurrentDictionary<IAssemblySymbol, Dictionary<ISymbol, ImmutableArray<string>>?> IndexCache { get; } =
        new(SymbolEqualityComparer.Default);

    // Foreach loop variable → element tags, populated by the loop analysis action and
    // consulted in GetAccessInfo for ILocalReferenceOperation. Separate from the
    // normal symbol resolution path because locals don't support attributes in C#,
    // so the tag is inferred from the collection being iterated.
    public ConcurrentDictionary<ILocalSymbol, ImmutableArray<string>> LocalBindings { get; } =
        new(SymbolEqualityComparer.Default);
}
