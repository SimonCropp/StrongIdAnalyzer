// NOTE: deliberately no `using StrongIdAnalyzer;` directive — relies on the
// generator still emitting the `global using StrongIdAnalyzer;` even when
// the IdAttribute type is already visible via the UpstreamLib project
// reference (UpstreamLib grants InternalsVisibleTo to this assembly).
public class UpstreamReferenceTests
{
    [Test]
    public void IdAttribute_Resolves_FromUpstream()
    {
        var holder = new UpstreamIdHolder { Reference = System.Guid.NewGuid() };
        Consume(holder.Reference);
    }

    [Test]
    public void IdAttribute_Constructable_WithoutExplicitUsing() =>
        _ = new IdAttribute("Order");

    [Test]
    public async Task GeneratedIdAttribute_ResolvesToUpstreamAssembly()
    {
        // The attribute type must come from UpstreamLib, not a re-emission in
        // this assembly — confirms the generator skipped emission but kept the
        // global using.
        var attributeType = typeof(IdAttribute);
        var upstreamAssembly = typeof(UpstreamIdHolder).Assembly;
        await Assert.That(attributeType.Assembly).IsEqualTo(upstreamAssembly);
    }

    static void Consume([Id("Order")] System.Guid value)
    {
    }
}
