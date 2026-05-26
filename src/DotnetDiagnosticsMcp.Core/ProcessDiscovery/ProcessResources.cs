namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// OS-level resource usage snapshot for a process: file descriptors / handles, TCP state and
/// open-file limits. Best-effort — partial reads degrade to Notes rather than failing.
/// </summary>
public sealed record ProcessResources(
    int ProcessId,
    DateTimeOffset CapturedAt,
    int? FdCount,
    int? HandleCount,
    FdBreakdown? Fd,
    SocketBreakdown? Sockets,
    RLimits? Limits,
    IReadOnlyList<string> Notes,
    ProcessResourcesTrend? Trend);

public sealed record FdBreakdown(
    int Sockets,
    int Regular,
    int Pipes,
    int Eventfds,
    int Other);

public sealed record SocketBreakdown(
    int Established,
    int TimeWait,
    int CloseWait,
    int Listen,
    int Other);

public sealed record RLimits(
    long? NoFileSoft,
    long? NoFileHard,
    double? NoFileUsageFraction);

public sealed record ProcessResourcesTrend(
    IReadOnlyList<ProcessResourcesSample> Samples);

public sealed record ProcessResourcesSample(
    DateTimeOffset Timestamp,
    int? FdCount,
    int? HandleCount,
    FdBreakdown? Fd,
    SocketBreakdown? Sockets,
    RLimits? Limits);
