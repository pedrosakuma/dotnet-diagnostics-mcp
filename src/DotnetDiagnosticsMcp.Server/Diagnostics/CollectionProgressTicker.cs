using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Diagnostics;

/// <summary>
/// Wraps a long-running diagnostic collection with periodic MCP <c>notifications/progress</c>
/// emissions, anchored on the request's <c>_meta.progressToken</c>. RFC 0002 §7.3 item 7 /
/// issue #211 — the MCP-native progress + cancel surface for bounded-time collectors.
/// </summary>
/// <remarks>
/// <para>
/// When the inbound request carries no progress token (the spec leaves it optional and many
/// older clients don't set it) the helper simply forwards to the inner work and the
/// underlying collector behaves exactly as before — no extra notifications, no extra cost.
/// </para>
/// <para>
/// Cancellation is observed end-to-end: the MCP request-bound token (which trips on
/// <c>notifications/cancelled</c>) flows into both the inner work and the ticker loop. The
/// ticker is best-effort — any exception raised by <c>NotifyProgressAsync</c> is swallowed so
/// a flaky transport never breaks the actual collection.
/// </para>
/// </remarks>
internal static class CollectionProgressTicker
{
    /// <summary>
    /// Runs <paramref name="work"/> while sending MCP progress notifications every
    /// <paramref name="interval"/>. The reported <c>Progress</c> value is clamped to
    /// [0, 100] (a percentage) and the <c>Total</c> field is set to 100 so clients can render
    /// a determinate progress bar.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        RequestContext<CallToolRequestParams>? request,
        string operation,
        TimeSpan totalDuration,
        TimeSpan interval,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var progressToken = request?.Params?.ProgressToken;
        var server = request?.Server;
        if (progressToken is null || server is null || totalDuration <= TimeSpan.Zero)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        var pt = progressToken.Value;
        var totalSeconds = totalDuration.TotalSeconds;
        var started = DateTimeOffset.UtcNow;

        using var stopTicker = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tickerTask = Task.Run(async () =>
        {
            try
            {
                // Anchor at 0% so clients see the operation start immediately.
                await SafeNotifyAsync(server, pt, 0, totalSeconds, operation, started, stopTicker.Token).ConfigureAwait(false);
                while (!stopTicker.IsCancellationRequested)
                {
                    try { await Task.Delay(interval, stopTicker.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
                    var pct = totalSeconds > 0 ? Math.Min(100.0, elapsed / totalSeconds * 100.0) : 0;
                    await SafeNotifyAsync(server, pt, pct, totalSeconds, operation, started, stopTicker.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort: a flaky progress channel must never break the inner collection.
            }
        }, CancellationToken.None);

        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { stopTicker.Cancel(); }
            catch (ObjectDisposedException) { /* race with using-scope teardown */ }
            try { await tickerTask.ConfigureAwait(false); }
            catch { /* already swallowed inside */ }

            // Final 100% notification so clients see the operation terminated. Best-effort —
            // never throws; runs even on cancel/exception paths.
            try
            {
                await server.NotifyProgressAsync(
                    pt,
                    new ProgressNotificationValue
                    {
                        Progress = 100f,
                        Total = 100,
                        Message = $"{operation}: complete",
                    },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }
    }

    private static async Task SafeNotifyAsync(
        McpServer server,
        ProgressToken pt,
        double percent,
        double totalSeconds,
        string operation,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
            await server.NotifyProgressAsync(
                pt,
                new ProgressNotificationValue
                {
                    Progress = (float)percent,
                    Total = 100,
                    Message = $"{operation}: {elapsed:F1}s / {totalSeconds:F0}s",
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
    }
}
