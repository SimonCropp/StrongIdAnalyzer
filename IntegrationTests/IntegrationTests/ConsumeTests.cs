public class ConsumeTests
{
    [Test]
    public void MatchingIdsBuildClean()
    {
        var sample = new IdSample();
        sample.AssignMatching();
    }

    [Test]
    public void KnownMismatchWithSuppressionBuildsClean()
    {
        var sample = new IdSample();
        sample.AssignSuppressedMismatch();
    }

    [Test]
    public void GeneratedIdAttribute_IsAvailable() =>
        // Compile-time: this line fails to build if the source generator did not emit
        // IdAttribute into the consumer compilation.
        _ = new IdAttribute("Order");

    [Test]
    public void GeneratedUnionIdAttribute_IsAvailable() =>
        _ = new UnionIdAttribute("Order", "Customer");

    [Test]
    public void IdTagTypeParameter_FlowsThroughLinqChain()
    {
        // Mirrors the CommitmentsDataModel shape: WellKnownId<Operation>.Guids is
        // implicitly tagged via [IdTag] T, flows through Except and Select, and
        // binds the lambda param to the substituted tag — no SIA002 at build time.
        var mapper = new RoleOperationMapper();
        mapper.Seed();
    }
}

public class Operation;

public static class WellKnownId<[IdTag] T>
{
    public static IEnumerable<System.Guid> Guids { get; } = [];
}

public class RoleOperationMapper
{
    [Id<Operation>]
    static System.Guid[] blocked = [];

    public List<Row> Seed() =>
        WellKnownId<Operation>
            .Guids
            .Except(blocked)
            .Select(_ => new Row { Target = _ })
            .ToList();
}

public class Row
{
    [Id<Operation>]
    public System.Guid Target { get; set; }
}

public class IdSample
{
    // OrderId / CustomerId are tagged "Order" / "Customer" by the naming convention.
    public System.Guid OrderId { get; set; }

    public System.Guid CustomerId { get; set; }

    public static void ConsumeOrderId([Id("Order")] System.Guid value)
    {
    }

    public void AssignMatching() =>
        ConsumeOrderId(OrderId);

#pragma warning disable SIA001
    public void AssignSuppressedMismatch() =>
        ConsumeOrderId(CustomerId);
#pragma warning restore SIA001
}
