namespace OpcUaBridge.Diagnostics;

/// <summary>What the bridge process has cost the machine since the previous sample.</summary>
/// <param name="Window">The period this sample covers.</param>
/// <param name="CpuPercentOfCore">Processor time over the window, as a percentage of one core.</param>
/// <param name="CpuPercentOfMachine">
/// The same time as a percentage of every core, which is the number Windows Task Manager
/// and most Linux monitors report.
/// </param>
/// <param name="WorkingSetBytes">Resident memory.</param>
/// <param name="ManagedHeapBytes">The managed heap after the last collection.</param>
/// <param name="ThreadCount">OS threads in the process.</param>
/// <param name="Gen0Collections">Gen-0 collections since the process started.</param>
/// <param name="Gen2Collections">Gen-2 collections since the process started.</param>
public sealed record ProcessCost(
    TimeSpan Window,
    double CpuPercentOfCore,
    double CpuPercentOfMachine,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int ThreadCount,
    int Gen0Collections,
    int Gen2Collections);
