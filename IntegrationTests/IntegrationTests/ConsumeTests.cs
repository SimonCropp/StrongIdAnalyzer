[TestFixture]
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
