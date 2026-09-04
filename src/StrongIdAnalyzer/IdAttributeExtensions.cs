// Recognition and reading of the Id-attribute family — `[Id]`, `[Id<T>]`, `[UnionId]`,
// `[UnionId<T1,T2>]`, `[IdTag]`. The attributes are source-generated as `internal` per
// assembly, so the same metadata name resolves to distinct symbols across compilations.
// Everything here therefore matches by metadata name + namespace chain rather than by
// symbol identity, which is what keeps cross-assembly tagging working.
static class IdAttributeExtensions
{
    public const string IdMetadataName = "IdAttribute";
    public const string UnionIdMetadataName = "UnionIdAttribute";
    public const string ExternalIdMetadataName = "ExternalIdAttribute";
    const string idTagMetadataName = "IdTagAttribute";
    const string idNamespace = "StrongIdAnalyzer";
    const string idGenericMetadataName = "IdAttribute`1";
    const string unionIdGenericMetadataPrefix = "UnionIdAttribute`";
    const int unionIdMaxGenericArity = 5;

    // Returns the symbol's [Id] attribute specifically — not [UnionId]. Used by the
    // SIA005 "redundant" check, which only applies to single-tag [Id("X")] values that
    // happen to equal what the convention would infer.
    public static AttributeData? GetExplicitIdAttribute(this ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.IsAnyId())
            {
                return attribute;
            }
        }

        return null;
    }

    // True when the symbol carries any Id-family attribute — [Id] or [UnionId]. Used by
    // SIA004 ambiguity tracking so explicitly-tagged declarations drop out of the pool
    // that convention alone would have collided.
    public static bool HasAnyIdFamilyAttribute(this ISymbol symbol)
    {
        if (symbol.GetAttributes().HasIdFamilyAttribute())
        {
            return true;
        }

        // A record primary-ctor parameter with [Id] / [UnionId] counts for the
        // synthesized property too — the attribute is physically on the parameter
        // (its default target) but the user means it to apply to both.
        if (symbol is IPropertySymbol property &&
            property.FindRecordPrimaryParameter() is { } parameter)
        {
            return parameter.GetAttributes().HasIdFamilyAttribute();
        }

        return false;
    }

    public static bool HasIdFamilyAttribute(this ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.IsAnyId() ||
                attribute.IsAnyUnionId())
            {
                return true;
            }
        }

        return false;
    }

    // Records: a property synthesized from a primary-ctor parameter carries the
    // parameter's [Id] / [UnionId] (the compiler leaves such attributes on the
    // parameter, which is their default target). Returns the parameter so callers
    // can read its attributes as if they were on the property.
    public static IParameterSymbol? FindRecordPrimaryParameter(this IPropertySymbol property)
    {
        var type = property.ContainingType;
        if (type is null || !type.IsRecord)
        {
            return null;
        }

        foreach (var constructor in type.InstanceConstructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.Name == property.Name &&
                    SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type))
                {
                    return parameter;
                }
            }
        }

        return null;
    }

    public static string? GetValue(this AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length > 0 &&
            attribute.ConstructorArguments[0].Value is string { Length: > 0 } value)
        {
            return value;
        }

        if (attribute.TryGetGenericIdTag(out var genericTag))
        {
            return genericTag;
        }

        return null;
    }
    // Matches by comparing the attribute class's short metadata name and walking its
    // containing namespace chain — avoids the string allocation of ToDisplayString.
    // Works across assembly boundaries where each assembly has its own internal copy
    // of the generated attribute.
    public static bool IsNamed(this AttributeData attribute, string typeName)
    {
        var attributeClass = attribute.AttributeClass;
        return attributeClass is not null &&
               attributeClass.MetadataName == typeName &&
               IsInIdNamespace(attributeClass.ContainingNamespace);
    }

    // Returns true when `ns` is the single-segment root namespace `StrongIdAnalyzer`.
    static bool IsInIdNamespace(INamespaceSymbol? ns) =>
        ns is { Name: idNamespace, ContainingNamespace.IsGlobalNamespace: true};

    // Matches `[Id<T>]` — the generic counterpart of `[Id("T")]`. Reads the tag from the
    // type argument's short name, mirroring `nameof(T)`. Open/error type arguments and
    // unresolved type parameters are rejected so malformed usages don't leak a tag.
    public static bool TryGetGenericIdTag(this AttributeData attribute, out string tag)
    {
        tag = "";
        var attributeClass = attribute.AttributeClass;
        if (attributeClass is null || attributeClass.Arity != 1)
        {
            return false;
        }

        var original = attributeClass.OriginalDefinition;
        if (original.MetadataName != idGenericMetadataName ||
            !IsInIdNamespace(original.ContainingNamespace))
        {
            return false;
        }

        var typeArgument = attributeClass.TypeArguments[0];
        if (typeArgument.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
        {
            return false;
        }

        var name = typeArgument.Name;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        tag = name;
        return true;
    }

    public static bool IsAnyId(this AttributeData attribute) =>
        attribute.IsNamed(IdMetadataName) ||
        attribute.TryGetGenericIdTag(out _);

    // Walk the containing-type chain of `symbol` and collect substituted type-argument
    // short names for every original-definition type parameter marked [IdTag]. The
    // attribute is opt-in at the type-parameter declaration, so this produces a tag set
    // only when the author explicitly marked a generic as an Id-tag source.
    //
    // Scope: only wired into the collection-element path (GetExplicitCollectionTags) so
    // `WellKnownId<Customer>.Guids` flows a tag through LINQ chains. Scalar members
    // (method returns, properties, parameters) still need explicit [Id] / [UnionId] —
    // otherwise every factory method inside a `[IdTag]`-annotated type would surface
    // SIA003 against callers storing the result into an untagged field.
    //
    // Skipped when the type argument is still a type parameter (open-generic reference
    // from inside the declaring type itself) or an error type — same guard TryGetGenericIdTag
    // uses, for the same reason: no real tag name is available yet.
    public static ImmutableArray<string> GetImplicitTagsFromContainingGenerics(this ISymbol symbol)
    {
        ImmutableArray<string>.Builder? builder = null;
        HashSet<string>? seen = null;
        var containing = symbol.ContainingType;
        while (containing is not null)
        {
            var originalParams = containing.OriginalDefinition.TypeParameters;
            var constructedArgs = containing.TypeArguments;
            var count = Math.Min(originalParams.Length, constructedArgs.Length);
            for (var i = 0; i < count; i++)
            {
                if (!HasIdTagAttribute(originalParams[i]))
                {
                    continue;
                }

                var arg = constructedArgs[i];
                if (arg.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
                {
                    continue;
                }

                var name = arg.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                seen ??= [with(StringComparer.Ordinal)];
                if (!seen.Add(name))
                {
                    continue;
                }

                builder ??= ImmutableArray.CreateBuilder<string>();
                builder.Add(name);
            }

            containing = containing.ContainingType;
        }

        return builder?.ToImmutable() ?? [];
    }

    public static bool HasIdTagAttribute(this ITypeParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.IsNamed(idTagMetadataName))
            {
                return true;
            }
        }

        return false;
    }

    // Matches `[UnionId<T1, T2, ...>]` (arities 2..5). Each type argument contributes its
    // short name as a tag, mirroring `[UnionId(nameof(T1), nameof(T2), ...)]`.
    public static bool TryGetGenericUnionIdTags(this AttributeData attribute, out ImmutableArray<string> tags)
    {
        tags = [];
        var attributeClass = attribute.AttributeClass;
        if (attributeClass is null)
        {
            return false;
        }

        var arity = attributeClass.Arity;
        if (arity is < 2 or > unionIdMaxGenericArity)
        {
            return false;
        }

        var original = attributeClass.OriginalDefinition;
        if (!IsInIdNamespace(original.ContainingNamespace) ||
            !IsUnionIdGenericMetadataName(original.MetadataName, arity))
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<string>(arity);
        foreach (var typeArgument in attributeClass.TypeArguments)
        {
            if (typeArgument.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
            {
                return false;
            }

            var name = typeArgument.Name;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            builder.Add(name);
        }

        tags = builder.ToImmutable();
        return true;
    }

    public static bool IsAnyUnionId(this AttributeData attribute) =>
        attribute.IsNamed(UnionIdMetadataName) ||
        attribute.TryGetGenericUnionIdTags(out _);

    // Allocation-free equivalent of `metadataName == "UnionIdAttribute`" + arity` for the
    // single-digit arities we support (2..unionIdMaxGenericArity).
    static bool IsUnionIdGenericMetadataName(string metadataName, int arity) =>
        metadataName.Length == unionIdGenericMetadataPrefix.Length + 1 &&
        metadataName[^1] == (char)('0' + arity) &&
        metadataName.StartsWith(unionIdGenericMetadataPrefix, StringComparison.Ordinal);
    // Reads `[UnionId(params string[] options)]`'s constructor argument. Roslyn surfaces
    // the `params string[]` as a single Array TypedConstant whose Values are the items.
    public static ImmutableArray<string> ExtractUnionOptions(this AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
        {
            return [];
        }

        var first = attribute.ConstructorArguments[0];
        if (first.Kind != TypedConstantKind.Array)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(first.Values.Length);
        foreach (var element in first.Values)
        {
            // Empty tags are dropped here — [Id("")] is never a valid shape, so
            // letting one through would propagate into diagnostics/codefixes that
            // round-trip the tag back into [Id("")] / [UnionId("", ...)] output.
            if (element.Value is string { Length: > 0 } s)
            {
                builder.Add(s);
            }
        }

        return builder.ToImmutable();
    }
}
