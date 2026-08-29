using PcCore.Capabilities.Abstractions;

namespace PcCore.Capabilities.System;

/// <summary>
/// Non-sensitive machine information. Deliberately excludes user name, domain, hardware serials,
/// network addresses and installed software — see SPEC.md 5.
/// </summary>
public sealed record SystemInfo
{
    public required string MachineName { get; init; }
    public required string OsDescription { get; init; }
    public required string OsVersion { get; init; }
    public required string OsArchitecture { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string PcCoreVersion { get; init; }
    public required string RuntimeVersion { get; init; }
    public required int ProcessorCount { get; init; }

    /// <summary>Machine uptime in seconds.</summary>
    public required long UptimeSeconds { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }
    public required DateTimeOffset TimestampLocal { get; init; }

    /// <summary>
    /// Reported so a caller can verify PC Core is running unelevated, as v0.1 intends.
    /// </summary>
    public required bool IsElevated { get; init; }
}

public sealed record ProcessEntry(int Pid, string Name, long? WorkingSetBytes);

public sealed record ProcessList(int Count, IReadOnlyList<ProcessEntry> Processes);

/// <summary>Platform hook for <c>system.get_info</c>.</summary>
public interface ISystemInfoProvider
{
    SystemInfo GetInfo();
}

/// <summary>Platform hook for <c>system.list_processes</c>.</summary>
public interface IProcessLister
{
    IReadOnlyList<ProcessEntry> ListProcesses();
}

public sealed class GetSystemInfoAction : CapabilityAction<NoParameters, SystemInfo>
{
    private readonly ISystemInfoProvider _provider;

    public GetSystemInfoAction(ISystemInfoProvider provider) => _provider = provider;

    public override string Name => "system.get_info";

    public override string Description => "Non-sensitive information about the machine and this PC Core build.";

    protected override ValueTask<SystemInfo> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(_provider.GetInfo());
}

public sealed class ListProcessesAction : CapabilityAction<NoParameters, ProcessList>
{
    private readonly IProcessLister _lister;

    public ListProcessesAction(IProcessLister lister) => _lister = lister;

    public override string Name => "system.list_processes";

    public override string Description => "Lightweight list of running processes (pid and name only).";

    protected override ValueTask<ProcessList> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var processes = _lister.ListProcesses();
        return ValueTask.FromResult(new ProcessList(processes.Count, processes));
    }
}
