// The csproj raises SIA001;SIA002;SIA003 to errors. The target below is in a namespace
// matched by `strongidanalyzer.suppressed_namespaces = ...,Suppressed*` in .editorconfig,
// so SIA003 must not fire on the untagged parameter — otherwise this file fails to build.
namespace Suppressed.Library;

public static class SuppressedEndpoint
{
    public static void Save(System.Guid id) { }
}

public class SuppressedNamespaceConsumeTests
{
    // OrderId picks up tag "Order" by naming convention — no explicit [Id] needed.
    public System.Guid OrderId { get; set; }

    [Test]
    public void TaggedSource_Into_UntaggedTarget_In_SuppressedNamespace_BuildsClean() =>
        SuppressedEndpoint.Save(OrderId);
}
