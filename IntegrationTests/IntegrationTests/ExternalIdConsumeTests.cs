// Consumes `[assembly: ExternalId]` through the packaged analyzer: the attribute has to
// be emitted by the packaged generator for this file to compile at all, and the mapped
// flow below has to resolve as "Process" → "Process" or the csproj's SIA001-as-error
// fails the build. Process.Id is a framework member, which the default namespace
// suppression leaves untagged unless a mapping says otherwise.
using System.Diagnostics;

[assembly: ExternalId(typeof(Process), nameof(Process.Id), "Process")]

public class ExternalIdConsumeTests
{
    // processId picks up tag "Process" by naming convention.
    static void Track(int processId) { }

    [Test]
    public void MappedFrameworkMember_FlowsIntoMatchingTarget_BuildsClean() =>
        Track(Process.GetCurrentProcess().Id);
}
