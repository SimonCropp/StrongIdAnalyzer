static class Extensions
{
    // Peels off conversions and `await` so the resolver sees the value-producing
    // operation underneath. An `await task` result carries the tag of the method that
    // produced the task, so unwrapping lets `[return: Id]` on an async method flow
    // through the await.
    public static IOperation Unwrap(this IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IAwaitOperation await:
                    operation = await.Operation;
                    continue;
                default:
                    return operation;
            }
        }
    }

    // Resolves the underlying declaration symbol for an expression. Returns null for
    // expression shapes that have no single declaration — literals, locals (no
    // attribute target), compound expressions, etc.
    public static ISymbol? GetReferencedSymbol(this IOperation operation) =>
        operation.Unwrap() switch
        {
            IPropertyReferenceOperation prop => prop.Property,
            IFieldReferenceOperation field => field.Field,
            IParameterReferenceOperation param => param.Parameter,
            IInvocationOperation invocation => invocation.TargetMethod,
            _ => null
        };

    // Returns the declared type of a value-producing symbol: property / field /
    // parameter / local → its Type; method → ReturnType. Other symbol kinds don't
    // have a single "declared value type" and return null.
    public static ITypeSymbol? GetDeclaredType(this ISymbol symbol) =>
        symbol switch
        {
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            IParameterSymbol pa => pa.Type,
            ILocalSymbol l => l.Type,
            IMethodSymbol m => m.ReturnType,
            _ => null
        };

    // Arrays are IEnumerable<T>. Otherwise a type must implement exactly one
    // IEnumerable<T> construction for the caller to pick a single element type.
    // Dictionary<K,V> implements IEnumerable<KeyValuePair<K,V>> (unique element type,
    // but composite — callers further gate on primitive-ish element types by requiring
    // the lambda's param type to match).
    public static ITypeSymbol? TryGetEnumerableElementType(this ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        // string implements IEnumerable<char> but is used as a scalar primitive ID type,
        // so exclude it from collection/element handling.
        if (named.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (named is
            {
                IsGenericType: true,
                ConstructedFrom.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T
            })
        {
            return named.TypeArguments[0];
        }

        ITypeSymbol? found = null;
        foreach (var iface in named.AllInterfaces)
        {
            if (iface is
                {
                    IsGenericType: true,
                    ConstructedFrom.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T
                })
            {
                if (found is not null &&
                    !SymbolEqualityComparer.Default.Equals(found, iface.TypeArguments[0]))
                {
                    return null;
                }

                found = iface.TypeArguments[0];
            }
        }

        return found;
    }

    // Yields the member and then every override/interface-impl target reachable from it.
    // `new`-hide is NOT followed (OverriddenProperty returns null for it) — the hidden
    // declaration is a fresh member that the user has explicitly disconnected from the
    // base. Parameters are single-tag today and don't pass through this enumerator.
    public static IEnumerable<ISymbol> EnumerateMemberChain(this ISymbol member)
    {
        yield return member;

        if (member is not IPropertySymbol property)
        {
            yield break;
        }

        var overridden = property.OverriddenProperty;
        while (overridden is not null)
        {
            yield return overridden;
            overridden = overridden.OverriddenProperty;
        }

        foreach (var ifaceMember in property.ExplicitInterfaceImplementations)
        {
            yield return ifaceMember;
        }

        var containingType = property.ContainingType;
        if (containingType is null)
        {
            yield break;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(property.Name).OfType<IPropertySymbol>())
            {
                var impl = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (SymbolEqualityComparer.Default.Equals(impl, property))
                {
                    yield return ifaceMember;
                }
            }
        }
    }

    public static bool HasIdMemberInChain(this INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                return false;
            }

            foreach (var member in current.GetMembers("Id"))
            {
                if (member is IPropertySymbol { IsIndexer: false } or IFieldSymbol { IsImplicitlyDeclared: false })
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Walks the type and any constructed-generic type arguments / element types looking
    // for an unsubstituted `ITypeParameterSymbol`. Used by `IsBoundaryTarget` to suppress
    // SIA003 when the target's declared shape still has a type parameter — bare `T`,
    // `TestEntity<T>`, `List<T>`, `Dictionary<Guid, T>`, `T[]`, etc.
    public static bool ContainsOpenTypeParameter(this ITypeSymbol type)
    {
        // ReSharper disable TailRecursiveCall
        switch (type.TypeKind)
        {
            case TypeKind.TypeParameter:
                return true;
            case TypeKind.Array:
                return ((IArrayTypeSymbol)type).ElementType.ContainsOpenTypeParameter();
            case TypeKind.Pointer:
                return ((IPointerTypeSymbol)type).PointedAtType.ContainsOpenTypeParameter();
        }
        // ReSharper restore TailRecursiveCall

        if (type is INamedTypeSymbol named)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (arg.ContainsOpenTypeParameter())
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static Location ToLocation(this SyntaxReference reference) =>
        Location.Create(reference.SyntaxTree, reference.Span);

    public static Location ResolveDeclarationLocation(this ISymbol? symbol)
    {
        var declaration = symbol?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration is null)
        {
            return Location.None;
        }

        return declaration.ToLocation();
    }
}
