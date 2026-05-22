using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// MCP CallTool filter that auto-routes diagnostic tool calls from an MCP session bound
/// to an active <see cref="InvestigationHandle"/> through the in-Pod diagnostics MCP
/// instead of executing them locally on the orchestrator. Closes the loop on P3b
/// (issue #153) so clients don't have to rewrite URLs to <c>/proxy/{handleId}/…</c>
/// after calling <c>attach_to_pod</c>.
/// </summary>
/// <remarks>
/// <para>
/// Short-circuit rules (any miss falls through to local execution via <c>next</c>):
/// </para>
/// <list type="number">
///   <item><description>The MCP session must have an <see cref="IInvestigationSessionBinder"/> binding.</description></item>
///   <item><description>The tool name is not in the orchestrator-management bypass list
///     (<c>list_pods</c>, <c>attach_to_pod</c>, …). Forwarding these would loop the request
///     back into a child orchestrator surface that doesn't know about Kubernetes.</description></item>
///   <item><description>The call arguments do not contain a non-null <c>processId</c> property —
///     an explicit pid is the P2 escape hatch (explicit &gt; binding), matching the
///     precedence rule documented on <c>IProcessContextResolver</c>.</description></item>
///   <item><description>The bound handle is still <see cref="InvestigationState.Active"/>.
///     Detach / TTL eviction / attach failure all immediately revert subsequent calls to
///     local resolution (acceptance criterion in issue #153).</description></item>
/// </list>
/// <para>
/// Forwarding errors do <em>not</em> fall through to local execution — that would mask
/// the routing bug and silently make a "diagnostics in pod X" question appear to be
/// answered by data from the orchestrator host. Failures surface as a structured
/// <see cref="CallToolResult"/> with <c>IsError=true</c>, identical to
/// <see cref="ToolErrorSurfaceFilter"/>'s envelope so the LLM has one error shape to
/// reason about.
/// </para>
/// </remarks>
internal static class InvestigationProxyCallToolFilter
{
    /// <summary>Tools that must always run locally — they configure the orchestrator itself.</summary>
    private static readonly HashSet<string> BypassToolNames = new(StringComparer.Ordinal)
    {
        "list_pods",
        "attach_to_pod",
        "detach_from_pod",
        "list_active_investigations",
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        IInvestigationSessionBinder sessionBinder,
        IInvestigationStore investigationStore,
        IInvestigationProxyClient proxyClient,
        Func<ILogger?> loggerAccessor,
        Func<RequestContext<CallToolRequestParams>, string?>? sessionIdResolver = null)
    {
        ArgumentNullException.ThrowIfNull(sessionBinder);
        ArgumentNullException.ThrowIfNull(investigationStore);
        ArgumentNullException.ThrowIfNull(proxyClient);
        ArgumentNullException.ThrowIfNull(loggerAccessor);

        sessionIdResolver ??= static ctx => TryGetServerSessionId(ctx.Server);

        return next => (request, cancellationToken) =>
        {
            var sessionId = sessionIdResolver(request);
            return InvokeAsync(
                request.Params,
                sessionId,
                next: (p, ct) => next(request, ct),
                sessionBinder, investigationStore, proxyClient, loggerAccessor,
                cancellationToken);
        };
    }

    /// <summary>
    /// Core short-circuit logic, decoupled from <see cref="RequestContext{T}"/> so unit
    /// tests can exercise every branch without constructing an <c>McpServer</c>. The
    /// <paramref name="next"/> delegate adapts back to the caller's underlying handler;
    /// in production it just forwards the original RequestContext.
    /// </summary>
    internal static async ValueTask<CallToolResult> InvokeAsync(
        CallToolRequestParams? requestParams,
        string? sessionId,
        Func<CallToolRequestParams?, CancellationToken, ValueTask<CallToolResult>> next,
        IInvestigationSessionBinder sessionBinder,
        IInvestigationStore investigationStore,
        IInvestigationProxyClient proxyClient,
        Func<ILogger?> loggerAccessor,
        CancellationToken cancellationToken)
    {
        var toolName = requestParams?.Name;
        if (string.IsNullOrEmpty(toolName) || BypassToolNames.Contains(toolName))
        {
            return await next(requestParams, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(sessionId))
        {
            return await next(requestParams, cancellationToken).ConfigureAwait(false);
        }

        var handleId = sessionBinder.TryGetHandleId(sessionId);
        if (string.IsNullOrEmpty(handleId))
        {
            return await next(requestParams, cancellationToken).ConfigureAwait(false);
        }

        if (HasExplicitProcessId(requestParams?.Arguments))
        {
            // Explicit > binding (P2 IProcessContextResolver precedence).
            return await next(requestParams, cancellationToken).ConfigureAwait(false);
        }

        var handle = investigationStore.GetById(handleId);
        if (handle is null || handle.State != InvestigationState.Active)
        {
            loggerAccessor()?.LogDebug(
                "Session {SessionId} bound to handle {HandleId} but handle is {State} — running '{ToolName}' locally.",
                sessionId, handleId, handle?.State.ToString() ?? "missing", toolName);
            return await next(requestParams, cancellationToken).ConfigureAwait(false);
        }

        // H6 / B3 review (issue #164): defense-in-depth owner check. The binder
        // is supposed to bind callers only to handles they own, but if a binding
        // ever drifts (e.g. session id rotation, a reuse code path that misses
        // the owner check) we must not forward upstream as someone else. When
        // the handle has an owner and it doesn't match the caller, surface a
        // structured error rather than silently widening access. Un-owned
        // handles (stdio / framework) remain forward-able by anyone.
        if (handle.OwnerSessionId is not null &&
            !string.Equals(handle.OwnerSessionId, sessionId, StringComparison.Ordinal))
        {
            loggerAccessor()?.LogWarning(
                "Refusing to forward '{ToolName}' from session {SessionId} via investigation {HandleId}: handle is owned by a different session.",
                toolName, sessionId, handle.HandleId);
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = $"Cannot forward '{toolName}' via investigation {handle.HandleId}: it is owned by a different MCP session. " +
                               "Re-attach to the pod in this session or call the tool locally with an explicit processId.",
                    },
                },
            };
        }

        // H7 (issue #164): enforce the explicit forwarded-tool allowlist BEFORE we
        // delegate to the proxy client. A tool that is not on the allowlist is NOT
        // silently demoted to local execution (which would mask "this tool doesn't
        // make sense in a Pod-attached context" misuse from the LLM); it surfaces
        // a structured error so the LLM can self-correct.
        if (!InvestigationProxyToolAllowlist.IsAllowed(toolName))
        {
            loggerAccessor()?.LogWarning(
                "Refusing to forward '{ToolName}' from session {SessionId} via investigation {HandleId}: tool is not on the proxy allowlist.",
                toolName, sessionId, handle.HandleId);
            return BuildToolNotAllowedResult(toolName, handle.HandleId);
        }

        try
        {
            loggerAccessor()?.LogDebug(
                "Forwarding '{ToolName}' from session {SessionId} via investigation {HandleId} ({Namespace}/{Pod}).",
                toolName, sessionId, handle.HandleId, handle.Namespace, handle.PodName);
            return await proxyClient.CallToolAsync(handle, requestParams!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsRethrowable(ex, cancellationToken))
        {
            loggerAccessor()?.LogWarning(
                ex,
                "Forwarding '{ToolName}' via investigation {HandleId} failed; returning structured error.",
                toolName, handle.HandleId);
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = BuildErrorText(toolName, handle.HandleId, ex) },
                },
            };
        }
    }

    /// <summary>Exposed for tests — mirror of <see cref="ToolErrorSurfaceFilter.IsRethrowable"/>.</summary>
    internal static bool IsRethrowable(Exception ex, CancellationToken cancellationToken)
        => (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
           || ex is McpProtocolException;

    /// <summary>Exposed for tests — returns true when arguments carry an explicit non-null <c>processId</c>.</summary>
    internal static bool HasExplicitProcessId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return false;
        if (!arguments.TryGetValue("processId", out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Number => true,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => false,
        };
    }

    /// <summary>Exposed for tests — formats the error block surfaced on forwarding failure.</summary>
    internal static string BuildErrorText(string toolName, string handleId, Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(toolName)
          .Append(" failed: proxy forwarding to investigation ")
          .Append(handleId)
          .Append(" via the in-Pod diagnostics MCP threw ")
          .Append(ex.GetType().Name)
          .Append(": ")
          .Append(string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message);

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

    /// <summary>
    /// H7 (issue #164) — structured error surface when a tool name fails the
    /// proxy allowlist gate. The shape mirrors <see cref="BuildErrorText"/> so the
    /// LLM has one error-block format to reason about.
    /// </summary>
    internal static CallToolResult BuildToolNotAllowedResult(string toolName, string handleId)
    {
        var text = $"{toolName} failed: tool '{toolName}' is not on the orchestrator's investigation-proxy " +
                   $"allowlist and was refused before forwarding to investigation {handleId}. " +
                   "Only the documented diagnostics tools (DiagnosticTools surface) can traverse the proxy. " +
                   "Either call this tool against a local pid (pass an explicit processId) or use a tool that is in the allowlist.";
        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = text },
            },
        };
    }

    // Mirror of OrchestratorTools.TryGetServerSessionId / DiagnosticTools.TryGetServerSessionId.
    // Reflection is used for consistency with the other call sites — though SessionId is
    // public on McpSession, the helpers everywhere else in this codebase route through
    // reflection so a SDK rename surfaces in one place. Returns null on stdio transport
    // and unit tests that synthesize an McpServer without a session.
    private static string? TryGetServerSessionId(McpServer? server)
        => server?.GetType()
                  .GetProperty("SessionId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                  ?.GetValue(server) as string;
}
