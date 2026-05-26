namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Captures a short "what requests are in-flight right now?" snapshot for an ASP.NET Core process.
/// </summary>
public interface IRequestsNowCollector
{
    Task<RequestsNowSnapshot> CollectAsync(
        int processId,
        TimeSpan window,
        int topFrames,
        CancellationToken cancellationToken = default);
}
