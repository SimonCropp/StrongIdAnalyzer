// Opt-in support for codebases that wrap the primitive in a type instead of tagging it:
// hand-rolled `readonly record struct UserId(Guid Value)`, StronglyTypedId (Andrew Lock),
// StrongTypedId (Steffen Skov), Vogen, and generic `Id<T>` shapes. A recognized wrapper
// IS a tag: members and expressions typed as the wrapper carry it, the member that
// exposes the primitive (`.Value` / `.PrimitiveValue`) carries it, and the constructor /
// factory parameter that accepts the primitive carries it. That is what lets a codebase
// migrate wrapper-by-wrapper — the analyzer checks the seams where a wrapper is unwrapped
// into, or built from, a tagged primitive — and what makes wrappers in referenced
// assemblies that cannot be migrated participate without any upstream attribute.
//
// Gated by .editorconfig key `strongidanalyzer.infer_wrapper_ids` (default false). With
// the flag off every member here returns false, so existing behavior is untouched.
//
// Recognition is structural and cached per type. Type-derived tags are reliable where
// name-derived ones are not, which is why they beat the naming convention but still lose
// to an explicit [Id] / [UnionId].
sealed class WrapperTypes(bool enabled, ImmutableArray<NamespacePattern> suppressedNamespaces)
{
    const string optionKey = "strongidanalyzer.infer_wrapper_ids";
    const string idSuffix = "Id";

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

    public bool Enabled { get; } = enabled;

    // Null entries mean "not a wrapper". Recognition recurses (type arguments, the value
    // member's type), so the TryGetValue / compute / TryAdd shape is used rather than a
    // GetOrAdd closure — re-entrancy on the same dictionary is safe that way.
    readonly ConcurrentDictionary<ITypeSymbol, WrapperInfo?> cache = new(SymbolEqualityComparer.Default);

    public bool TryGet(ITypeSymbol? type, out WrapperInfo info)
    {
        info = null!;
        if (!Enabled || UnwrapNullable(type) is not { } named)
        {
            return false;
        }

        if (!cache.TryGetValue(named, out var cached))
        {
            cached = Recognize(named);
            cache.TryAdd(named, cached);
        }

        if (cached is null)
        {
            return false;
        }

        info = cached;
        return true;
    }

    // `UserId?` is still a UserId as far as identity goes. Nullable itself is never a
    // wrapper — it lives in System, and its Value member is the wrapper, not a primitive.
    static INamedTypeSymbol? UnwrapNullable(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1 &&
            named.TypeArguments[0] is INamedTypeSymbol inner)
        {
            return inner;
        }

        return named;
    }

    WrapperInfo? Recognize(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Struct or TypeKind.Class) ||
            type.IsAnonymousType ||
            type.IsTupleType ||
            type.IsStatic ||
            type.IsImplicitlyDeclared ||
            type.SpecialType != SpecialType.None ||
            type.Name.Length == 0)
        {
            return null;
        }

        // An open `Id<T>` inside generic code has no domain to name yet; the caller that
        // closes T will see the constructed type and resolve that one instead.
        foreach (var argument in type.TypeArguments)
        {
            if (argument.ContainsOpenTypeParameter())
            {
                return null;
            }
        }

        if (type.TryGetEnumerableElementType() is not null)
        {
            return null;
        }

        // Keeps Lazy<T>, Task<T> and friends out — and keeps the KnownTags walk from
        // inspecting the members of every framework type.
        if (NamespaceSuppression.IsSuppressed(type, suppressedNamespaces))
        {
            return null;
        }

        // Name check first: it is what lets the compilation-wide walk in CollectKnownTags
        // skip most types before touching their members or attributes.
        var hasSuffix = HasIdSuffix(type.Name);
        var isGenericId = type.Name == idSuffix && type.IsGenericType;
        if (!hasSuffix && !isGenericId && !HasLibraryMarker(type))
        {
            return null;
        }

        var valueMember = FindValueMember(type);
        if (valueMember?.GetDeclaredType() is not { } valueType)
        {
            return null;
        }

        // A struct wrapping a wrapper is not itself an id; `outer.Value` reads as the
        // inner wrapper's tag through the normal member rules.
        if (TryGet(valueType, out _))
        {
            return null;
        }

        var tag = ResolveTag(type, valueType, hasSuffix);
        if (tag is null)
        {
            return null;
        }

        return new(type, tag, valueMember, valueType);
    }

    static bool HasIdSuffix(string name) =>
        name.Length > idSuffix.Length &&
        name.EndsWith(idSuffix, StringComparison.Ordinal);

    // The single public instance member exposing the primitive. Walks the base chain
    // reading members off the constructed base symbol, so an inherited generic member
    // (Skov's `TPrimitive PrimitiveValue` on StrongTypedValue<TSelf, TPrimitive>) shows
    // its substituted type. Metadata symbols only list declared members, which is the
    // other reason the walk is needed. Only primitive-shaped members count as candidates,
    // so hand-rolled extras like `bool IsEmpty` do not disqualify a wrapper; when several
    // remain, the conventional `Value` / `PrimitiveValue` name decides.
    static ISymbol? FindValueMember(INamedTypeSymbol type)
    {
        ISymbol? single = null;
        ISymbol? conventional = null;
        var count = 0;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)
            {
                break;
            }

            foreach (var member in current.GetMembers())
            {
                if (!IsValueCandidate(member))
                {
                    continue;
                }

                count++;
                single = member;
                if (conventional is null &&
                    member.Name is "Value" or "PrimitiveValue")
                {
                    conventional = member;
                }
            }
        }

        return count == 1 ? single : conventional;
    }

    // Accessibility rather than IsImplicitlyDeclared: a metadata record struct reports its
    // private backing field as explicitly declared, but it is never public.
    static bool IsValueCandidate(ISymbol member)
    {
        if (member.IsStatic ||
            member.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        ITypeSymbol memberType;
        switch (member)
        {
            case IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                when property.ExplicitInterfaceImplementations.IsEmpty:
                memberType = property.Type;
                break;
            case IFieldSymbol { IsConst: false } field:
                memberType = field.Type;
                break;
            default:
                return false;
        }

        return memberType.SpecialType == SpecialType.System_String ||
               (memberType.IsValueType && memberType.SpecialType != SpecialType.System_Boolean);
    }

    // `UserId` -> "User". Generic shapes name the domain through a type argument:
    // `Id<User>` / `EntityId<User>` -> "User", and a CRTP base such as
    // `StrongTypedGuid<CustomerId>` takes the tag of the wrapper it is parameterised
    // with, so the base's own constructor and inherited value member resolve to
    // "Customer". A type argument equal to the value type parameterises the primitive,
    // not the domain (`Id<Guid>`), and never contributes. Marker-admitted names without
    // the suffix keep the whole name.
    string? ResolveTag(INamedTypeSymbol type, ITypeSymbol valueType, bool hasSuffix)
    {
        if (type.IsGenericType)
        {
            ITypeSymbol? remaining = null;
            var remainingCount = 0;
            foreach (var argument in type.TypeArguments)
            {
                if (SymbolEqualityComparer.Default.Equals(argument, valueType))
                {
                    continue;
                }

                if (TryGet(argument, out var argumentWrapper))
                {
                    return argumentWrapper.Tag;
                }

                remaining = argument;
                remainingCount++;
            }

            if (remainingCount != 1 ||
                remaining is not INamedTypeSymbol { Name.Length: > 0 } domain ||
                domain.TypeKind == TypeKind.Error)
            {
                return null;
            }

            return domain.Name;
        }

        if (hasSuffix)
        {
            return type.Name.Substring(0, type.Name.Length - idSuffix.Length);
        }

        return type.Name;
    }

    // Library shapes whose names need not end in `Id`. Matched by name and namespace
    // chain, never by symbol identity: StronglyTypedId's attribute is [Conditional] and
    // only visible in source, so its metadata footprint is the [GeneratedCode] stamp; Skov's
    // marker interfaces survive into metadata as-is. IStrongTypedValue is deliberately not
    // a marker — `EmailAddress : StrongTypedValue<EmailAddress, string>` is a value
    // object, not an id.
    static bool HasLibraryMarker(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.OriginalDefinition is not { } attributeClass)
            {
                continue;
            }

            if (attributeClass.Name == "StronglyTypedIdAttribute" &&
                IsInNamespace(attributeClass.ContainingNamespace, "StronglyTypedIds"))
            {
                return true;
            }

            if (attributeClass.Name == "ValueObjectAttribute" &&
                IsInNamespace(attributeClass.ContainingNamespace, "Vogen"))
            {
                return true;
            }

            if (attributeClass.Name == "GeneratedCodeAttribute" &&
                IsInNamespace(attributeClass.ContainingNamespace, "System", "CodeDom", "Compiler") &&
                attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is "StronglyTypedId")
            {
                return true;
            }
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented.Name == "IStrongTypedId" &&
                IsInNamespace(implemented.ContainingNamespace, "StrongTypedId"))
            {
                return true;
            }
        }

        return false;
    }

    // True when `namespaceSymbol` is exactly the namespace spelled by `segments` from the root.
    static bool IsInNamespace(INamespaceSymbol? namespaceSymbol, params string[] segments)
    {
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (namespaceSymbol is null || namespaceSymbol.IsGlobalNamespace || namespaceSymbol.Name != segments[index])
            {
                return false;
            }

            namespaceSymbol = namespaceSymbol.ContainingNamespace;
        }

        return namespaceSymbol is { IsGlobalNamespace: true };
    }

    // The tag a property / field / parameter carries because of a wrapper, in order:
    //   1. its declared type is a wrapper (static members, indexers and tuple fields alike);
    //   2. it is the value member of a wrapper — resolved from the receiver's static type
    //      when an access site supplies one, else from the containing type, else from a
    //      wrapper type argument of the containing type (Skov's inherited PrimitiveValue
    //      on StrongTypedValue<CustomerId, Guid>);
    //   3. it is the primitive-typed parameter of the wrapper's constructor, or of a
    //      static method declared on the wrapper (or a base of it) that returns the
    //      wrapper — `new UserId(g)`, `UserId.From(g)`, `StrongTypedValue<..>.Create(g)`.
    // Not gated on DeclaringSyntaxReferences: metadata wrappers are the point.
    public bool TryGetSymbolTag(ISymbol symbol, ITypeSymbol? receiverType, out string tag)
    {
        tag = "";
        if (!Enabled)
        {
            return false;
        }

        switch (symbol)
        {
            case IPropertySymbol or IFieldSymbol:
                if (TryGet(symbol.GetDeclaredType(), out var memberWrapper))
                {
                    tag = memberWrapper.Tag;
                    return true;
                }

                if (TryGetValueOwner(symbol, receiverType, out var valueOwner))
                {
                    tag = valueOwner.Tag;
                    return true;
                }

                return false;
            case IParameterSymbol parameter:
                if (TryGet(parameter.Type, out var parameterWrapper))
                {
                    tag = parameterWrapper.Tag;
                    return true;
                }

                if (TryGetWrapOwner(parameter, out var wrapOwner))
                {
                    tag = wrapOwner.Tag;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    // True when `member` is the value member of the wrapper that `receiverType` denotes.
    public bool IsValueMember(ISymbol member, ITypeSymbol? receiverType) =>
        TryGet(receiverType, out var wrapper) &&
        IsValueMemberOf(wrapper, member);

    bool TryGetValueOwner(ISymbol member, ITypeSymbol? receiverType, out WrapperInfo owner)
    {
        if (TryGet(receiverType, out owner) &&
            IsValueMemberOf(owner, member))
        {
            return true;
        }

        var containing = member.ContainingType;
        if (TryGet(containing, out owner) &&
            IsValueMemberOf(owner, member))
        {
            return true;
        }

        if (containing is { IsGenericType: true })
        {
            foreach (var argument in containing.TypeArguments)
            {
                if (TryGet(argument, out owner) &&
                    IsValueMemberOf(owner, member))
                {
                    return true;
                }
            }
        }

        owner = null!;
        return false;
    }

    static bool IsValueMemberOf(WrapperInfo wrapper, ISymbol member) =>
        SymbolEqualityComparer.Default.Equals(wrapper.ValueMember.OriginalDefinition, member.OriginalDefinition);

    bool TryGetWrapOwner(IParameterSymbol parameter, out WrapperInfo owner)
    {
        owner = null!;
        if (parameter.ContainingSymbol is not IMethodSymbol method)
        {
            return false;
        }

        if (method.MethodKind == MethodKind.Constructor)
        {
            if (!TryGet(method.ContainingType, out owner))
            {
                return false;
            }
        }
        else if (method.IsStatic)
        {
            // A repository's `static UserId FindOwner(Guid orderId)` returns the wrapper
            // but is not declared on it — its parameter keeps the naming convention.
            if (!TryGet(method.ReturnType, out owner) ||
                !DerivesFromOrIs(owner.Type, method.ContainingType))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(parameter.Type, owner.ValueType);
    }

    static bool DerivesFromOrIs(INamedTypeSymbol wrapper, INamedTypeSymbol? containing)
    {
        if (containing is null)
        {
            return false;
        }

        var definition = containing.OriginalDefinition;
        for (var current = wrapper; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
            {
                return true;
            }
        }

        return false;
    }

    // Parameters of methods declared on a wrapper (or on a base a wrapper is built from)
    // that are not the wrap boundary — `Parse(string input)`, `TryFormat(Span<char> ..)`,
    // `Equals(object obj)`. They are neither tagged nor untagged from the analyzer's point
    // of view: a NotPresent here would put a SIA003 fix site inside the wrapper.
    public bool IsWrapperOwned(ISymbol symbol)
    {
        if (!Enabled ||
            symbol is not IParameterSymbol { ContainingSymbol: IMethodSymbol { ContainingType: { } containing } })
        {
            return false;
        }

        if (TryGet(containing, out _))
        {
            return true;
        }

        if (!containing.IsGenericType)
        {
            return false;
        }

        foreach (var argument in containing.TypeArguments)
        {
            if (TryGet(argument, out var wrapper) &&
                DerivesFromOrIs(wrapper.Type, containing))
            {
                return true;
            }
        }

        return false;
    }

    // The tag an expression carries purely because of its static type — the one place
    // where the "locals, invocations and compound expressions are Unknown" policy is
    // relaxed. That policy exists because a name cannot vouch for a value; a wrapper type
    // can. `await` is peeled by Unwrap, so the awaited task's result type is read from
    // the original operation.
    public bool TryGetExpressionTag(IOperation original, IOperation unwrapped, out string tag)
    {
        tag = "";
        if (!Enabled)
        {
            return false;
        }

        var type = original is IAwaitOperation ? original.Type : unwrapped.Type;
        if (!TryGet(type, out var wrapper))
        {
            return false;
        }

        tag = wrapper.Tag;
        return true;
    }

    // A wrapper flowing as itself — into its own type, a base, an interface it
    // implements, `object`, a substituted `T` — leaks no primitive, so there is nothing
    // to report: the compiler already type-checked that flow. Anything that needs a
    // user-defined conversion (an implicit operator to Guid) is a real unwrap and is
    // deliberately not intact.
    public bool TravelsIntact(ITypeSymbol? sourceType, ITypeSymbol? targetType, Compilation compilation)
    {
        if (!Enabled ||
            sourceType is null ||
            targetType is null ||
            sourceType.TypeKind == TypeKind.Error ||
            targetType.TypeKind == TypeKind.Error ||
            !TryGet(sourceType, out _))
        {
            return false;
        }

        var conversion = compilation.ClassifyCommonConversion(sourceType, targetType);
        return conversion.Exists &&
               conversion.IsImplicit &&
               !conversion.IsUserDefined;
    }
}
