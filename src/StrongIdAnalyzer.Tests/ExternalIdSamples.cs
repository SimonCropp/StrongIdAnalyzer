// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedParameter.Global
// ReSharper disable ClassNeverInstantiated.Global
#pragma warning disable CA1822
#pragma warning disable SIA001

// Readme sample for `[assembly: ExternalId]`. Lives in its own file because an assembly
// attribute has to precede every namespace and type declaration in the file.

#region ExternalIdSample

using System.Diagnostics;

// Process.Id is declared in the framework, so nothing can put [Id] on it. The assembly
// attribute tags it from this side: read through Process (or a derived type), Id is a
// "Process" id.
[assembly: ExternalId(typeof(Process), nameof(Process.Id), "Process")]

namespace ExternalIdSample
{
    public class JobRunner
    {
        // id: "Process" (rule 2)
        public int ProcessId { get; set; }

        // id: "Job" (rule 2)
        public int JobId { get; set; }

        public void Track(Process process)
        {
            // OK: "Process" flows to "Process"
            ProcessId = process.Id;

            // SIA001: property 'Process.Id' is [Id("Process")] and flows to property
            // 'JobRunner.JobId', which is [Id("Job")]
            JobId = process.Id;
        }
    }
}

#endregion
