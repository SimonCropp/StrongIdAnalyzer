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
    public void GeneratedIdAttribute_IsAvailable()
    {
        // Compile-time: this line fails to build if the source generator did not emit
        // IdAttribute into the consumer compilation.
        var attr = new IdAttribute("Order");
        AreEqual("Order", attr.Type);
    }
}

public class IdSample
{
    [Id("Order")]
    public System.Guid OrderId { get; set; }

    [Id("Customer")]
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
