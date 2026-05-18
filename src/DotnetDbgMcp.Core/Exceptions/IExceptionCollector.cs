namespace DotnetDbgMcp.Core.Exceptions;

/// <summary>
/// Captures managed exceptions thrown by the target process over a fixed time window
/// via the <c>Microsoft-Windows-DotNETRuntime</c> EventPipe provider (Exception keyword).
/// </summary>
public interface IExceptionCollector
{
    Task<ExceptionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default);
}
