// `[assembly: ExternalId(typeof(DirectoryObject), nameof(DirectoryObject.Id), "EntraObject")]`
// — the consumer's way to tag a property or field it does not own. Nothing in a referenced
// assembly can carry [Id], and its `Id` members are no longer given a convention tag once
// the assembly or namespace is suppressed, so without this a Graph `User.Id` is simply
// invisible to the analyzer. A mapping says "read or written through `type` or anything
// derived from it, `member` carries these ids".
//
// Resolution mirrors the receiver-type walk: starting at the receiver's static type (or
// the member's declaring type when there is no receiver) walk the base chain, then the
// interfaces, and union every mapping hit, most-derived first. The result is explicit —
// the user wrote it down — so it beats a library-shipped index, the member's own
// attributes, and every convention, and SIA002/SIA003 fixes propose exactly these ids.
//
// Attributes are read from the compilation's own assembly and from every referenced
// assembly, so a mapping declared once in a shared project reaches its consumers.
sealed class ExternalIds
{
    public readonly struct Entry(
        AttributeData attribute,
        INamedTypeSymbol type,
        string member,
        ImmutableArray<string> tags)
    {
        public AttributeData Attribute { get; } = attribute;
        public INamedTypeSymbol Type { get; } = type;
        public string Member { get; } = member;
        public ImmutableArray<string> Tags { get; } = tags;
    }

    public static readonly ExternalIds Empty = new([], []);

    readonly Dictionary<(INamedTypeSymbol Type, string Member), ImmutableArray<string>> map;

    // Entries declared on the compilation's own assembly — the only ones the user can
    // edit, so the only ones validated at compilation end.
    public ImmutableArray<Entry> SourceEntries { get; }

    ExternalIds(ImmutableArray<Entry> sourceEntries, ImmutableArray<Entry> referencedEntries)
    {
        SourceEntries = sourceEntries;
        map = new(KeyComparer.Instance);
        // Source first so a consumer's mapping wins over one inherited from a reference;
        // repeats for the same key union, so two attributes can build a multi-id set.
        foreach (var entry in sourceEntries)
        {
            Add(entry);
        }

        foreach (var entry in referencedEntries)
        {
            Add(entry);
        }
    }

    void Add(Entry entry)
    {
        if (entry.Tags.IsEmpty)
        {
            return;
        }

        var key = (entry.Type.OriginalDefinition, entry.Member);
        if (!map.TryGetValue(key, out var existing))
        {
            map[key] = entry.Tags;
            return;
        }

        var merged = existing;
        foreach (var tag in entry.Tags)
        {
            if (!merged.Contains(tag))
            {
                merged = merged.Add(tag);
            }
        }

        map[key] = merged;
    }

    public bool IsEmpty => map.Count == 0;

    public IEnumerable<string> AllTags => map.Values.SelectMany(_ => _);

    public static ExternalIds Read(Compilation compilation)
    {
        var source = Collect(compilation.Assembly.GetAttributes());
        var referenced = ImmutableArray.CreateBuilder<Entry>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            referenced.AddRange(Collect(assembly.GetAttributes()));
        }

        if (source.IsEmpty && referenced.Count == 0)
        {
            return Empty;
        }

        return new(source, referenced.ToImmutable());
    }

    static ImmutableArray<Entry> Collect(ImmutableArray<AttributeData> attributes)
    {
        ImmutableArray<Entry>.Builder? builder = null;
        foreach (var attribute in attributes)
        {
            if (!attribute.IsNamed(IdAttributeExtensions.ExternalIdMetadataName))
            {
                continue;
            }

            var arguments = attribute.ConstructorArguments;
            if (arguments.Length != 3 ||
                arguments[0].Kind != TypedConstantKind.Type ||
                arguments[0].Value is not INamedTypeSymbol { TypeKind: not TypeKind.Error } type)
            {
                // A `typeof` that did not bind is already a compiler error.
                continue;
            }

            var member = arguments[1].Value as string ?? "";
            builder ??= ImmutableArray.CreateBuilder<Entry>();
            builder.Add(new(attribute, type, member, ReadTags(arguments[2])));
        }

        return builder?.ToImmutable() ?? [];
    }

    // `params string[] ids` arrives as one Array constant. Blank entries are dropped here
    // (as ExtractUnionOptions does) and reported by validation, not silently kept.
    static ImmutableArray<string> ReadTags(TypedConstant constant)
    {
        if (constant.Kind != TypedConstantKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(constant.Values.Length);
        foreach (var element in constant.Values)
        {
            if (element.Value is string tag &&
                !string.IsNullOrWhiteSpace(tag) &&
                !builder.Contains(tag))
            {
                builder.Add(tag);
            }
        }

        return builder.ToImmutable();
    }

    // The ids for `member` accessed through `receiverType` (null: through its declaring
    // type). Properties and fields only — the only shapes an [ExternalId] can name.
    public bool TryGetSymbolTags(ISymbol member, ITypeSymbol? receiverType, out ImmutableArray<string> tags)
    {
        tags = default;
        if (map.Count == 0 ||
            member is not (IPropertySymbol { IsIndexer: false } or IFieldSymbol))
        {
            return false;
        }

        var start = receiverType as INamedTypeSymbol ?? member.ContainingType;
        if (start is null)
        {
            return false;
        }

        ImmutableArray<string>.Builder? builder = null;
        for (var current = start; current is not null; current = current.BaseType)
        {
            Append(current, member.Name, ref builder);
        }

        foreach (var iface in start.AllInterfaces)
        {
            Append(iface, member.Name, ref builder);
        }

        if (builder is null)
        {
            return false;
        }

        tags = builder.ToImmutable();
        return true;
    }

    void Append(INamedTypeSymbol type, string member, ref ImmutableArray<string>.Builder? builder)
    {
        if (!map.TryGetValue((type.OriginalDefinition, member), out var found))
        {
            return;
        }

        builder ??= ImmutableArray.CreateBuilder<string>();
        foreach (var tag in found)
        {
            if (!builder.Contains(tag))
            {
                builder.Add(tag);
            }
        }
    }

    sealed class KeyComparer : IEqualityComparer<(INamedTypeSymbol Type, string Member)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((INamedTypeSymbol Type, string Member) x, (INamedTypeSymbol Type, string Member) y) =>
            x.Member == y.Member &&
            SymbolEqualityComparer.Default.Equals(x.Type, y.Type);

        public int GetHashCode((INamedTypeSymbol Type, string Member) obj) =>
            unchecked(SymbolEqualityComparer.Default.GetHashCode(obj.Type) * 31 + obj.Member.GetHashCode());
    }
}
