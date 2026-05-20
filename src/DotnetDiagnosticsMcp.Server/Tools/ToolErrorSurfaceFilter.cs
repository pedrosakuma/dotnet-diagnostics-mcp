using System.Text;
using DotnetDiagnosticsMcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// MCP CallTool filter that catches any unhandled exception from a tool invocation
/// and converts it to a structured <see cref="CallToolResult"/> with <c>IsError=true</c>
/// and a text block describing the exception type, message and inner-exception chain.
/// </summary>
/// <remarks>
/// <para>
/// Without this filter the MCP SDK's terminal stage swallows the original exception and
/// emits the generic <c>"An error occurred invoking 'X'."</c> message (see
/// <c>McpServer.ConfigureTools</c> in ModelContextProtocol.Core 1.3.0). That leaves the
/// LLM blind to the actual failure — PTRACE permission denied, FileNotFound, ClrMD
/// version mismatch, etc. all look identical. Issues #62, #63 surfaced this as a hard
/// blocker during dogfood.
/// </para>
/// <para>
/// The filter sits OUTSIDE the SDK's terminal try/catch (filters wrap the inner handler
/// while the terminal stage wraps the whole filter pipeline), so it observes exceptions
/// raised by the tool body before the SDK gets a chance to mask them. The filter only
/// kicks in when a tool fails to surface its own structured <see cref="DiagnosticResult{T}"/>
/// — tools that already classify (<c>GuardAttachAsync</c>) keep producing their richer
/// envelope. Cancellation and protocol exceptions are rethrown so the SDK can perform
/// the canonical close-up.
/// </para>
/// <para>
/// The error response intentionally carries no <c>StructuredContent</c>: the output
/// schema is the tool's success-path schema (e.g. <c>LiveHeapInspection</c>), and strict
/// clients (Copilot CLI, Claude Code) validate <c>structuredContent</c> against it. A
/// text-only error result honours <c>isError=true</c> without triggering schema
/// validation failures — same reasoning behind issue #61.
/// </para>
/// </remarks>
internal static class ToolErrorSurfaceFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(Func<ILogger?> loggerAccessor)
        => next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsRethrow(ex, cancellationToken))
            {
                var toolName = request.Params?.Name ?? "(unknown tool)";

                loggerAccessor()?.LogWarning(
                    ex,
                    "Tool '{ToolName}' threw {ExceptionType}; surfacing structured error to client.",
                    toolName,
                    ex.GetType().FullName ?? ex.GetType().Name);

                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = BuildErrorText(toolName, ex) },
                    },
                };
            }
        };

    private static bool IsRethrow(Exception ex, CancellationToken cancellationToken)
        => IsRethrowable(ex, cancellationToken);

    /// <summary>Exposed for tests — same predicate the filter uses to decide rethrow vs surface.</summary>
    internal static bool IsRethrowable(Exception ex, CancellationToken cancellationToken)
        => (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
           || ex is McpProtocolException;

    /// <summary>Exposed for tests — formats the error block surfaced as the text content.</summary>
    internal static string BuildErrorText(string toolName, Exception ex)
    {
        var topMessage = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message;
        var sb = new StringBuilder();
        sb.Append(toolName)
          .Append(" failed: ")
          .Append(ex.GetType().Name)
          .Append(": ")
          .Append(topMessage);

        sb.Append("\n\nException chain:");
        var depth = 0;
        for (Exception? cur = ex; cur is not null && depth < 8; cur = cur.InnerException, depth++)
        {
            sb.Append("\n  ")
              .Append(new string(' ', depth * 2))
              .Append(cur.GetType().FullName ?? cur.GetType().Name)
              .Append(": ")
              .Append(string.IsNullOrWhiteSpace(cur.Message) ? "(no message)" : cur.Message);
        }
        return sb.ToString();
    }
}
