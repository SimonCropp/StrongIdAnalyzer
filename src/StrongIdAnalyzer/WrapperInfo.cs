// A recognized "wrap the primitive in a type" id: the wrapper type, the tag it stands
// for, and the one member that exposes the primitive underneath. Produced once per type
// by WrapperTypes and shared for the rest of the compilation.
sealed class WrapperInfo(
    INamedTypeSymbol type,
    string tag,
    ISymbol valueMember,
    ITypeSymbol valueType)
{
    public INamedTypeSymbol Type { get; } = type;
    public string Tag { get; } = tag;
    public ISymbol ValueMember { get; } = valueMember;
    public ITypeSymbol ValueType { get; } = valueType;
}
