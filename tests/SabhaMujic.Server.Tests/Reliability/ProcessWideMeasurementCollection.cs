namespace SabhaMujic.Server.Tests.Reliability;

/// <summary>Tests that measure process-wide resources (handles, threads, memory) run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideMeasurementCollection
{
    public const string Name = "Process-wide measurements";
}
