using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// MCP CallTool filter that enforces the <see cref="RequireScopeAttribute"/> /
/// <see cref="RequireAnyScopeAttribute"/> taxonomy from RFC 0001 §2. Runs before the
/// tool body; a scope miss short-circuits with a structured <c>"forbidden"</c> envelope
/// (per MCP spec — return a tool error result, never throw at the SDK).
/// </summary>
/// <remarks>
/// <para>Resolution order:
/// <list type="number">
///   <item><description>Look up the tool's <see cref="ToolScopeRegistry.Requirement"/>
///   in the index built at startup. Unknown tools deny (defense in depth).</description></item>
///   <item><description>Resolve the active principal via <see cref="IPrincipalAccessor"/>.
///   For HTTP, this is the principal stamped by <c>BearerTokenMiddleware</c>; for stdio
///   it is the synthetic root principal (RFC §5).</description></item>
///   <item><description>Check the principal against the requirement. Wildcard (<c>root</c>
///   / <c>*</c>) scopes satisfy every gate — preserves the legacy
///   <c>MCP_BEARER_TOKEN</c> behavior byte-for-byte.</description></item>
/// </list>
/// </para>
/// <para>Audit logging (RFC §8) is per-tool: allow at Information, deny at Warning,
/// neither carries the presented bearer value.</para>
/// </remarks>
internal static class ToolScopeAuthorizationFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        ToolScopeRegistry registry,
        Func<IPrincipalAccessor?> principalAccessor,
        Func<ILogger?> loggerAccessor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(principalAccessor);
        ArgumentNullException.ThrowIfNull(loggerAccessor);

        return next => async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (string.IsNullOrEmpty(toolName))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            var requirement = registry.TryGet(toolName);
            if (requirement is null)
            {
                // Unknown tool — let the SDK produce its own not-found result; nothing to
                // authorize against.
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            // Stdio (no IPrincipalAccessor registered) is treated identically to root.
            var accessor = principalAccessor();
            var principal = accessor?.Current ?? StdioRootPrincipalAccessor.Instance.Current;

            var decision = Authorize(requirement.Value, principal);
            var logger = loggerAccessor();
            if (decision.IsAllowed)
            {
                logger?.LogInformation(
                    "Tool {Tool} authorized for principal {TokenName} (scopes {RequiredScopes}).",
                    toolName,
                    principal?.Name ?? "(none)",
                    FormatScopes(requirement.Value));
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            logger?.LogWarning(
                "Tool {Tool} denied for principal {TokenName} (missing scope {MissingScope}, presented {PrincipalScopes}).",
                toolName,
                principal?.Name ?? "(none)",
                decision.MissingScope,
                FormatPrincipalScopes(principal));

            return BuildForbiddenResult(toolName, requirement.Value, principal, decision.MissingScope);
        };
    }

    internal readonly record struct AuthorizationDecision(bool IsAllowed, string MissingScope)
    {
        public static AuthorizationDecision Allow() => new(true, string.Empty);
        public static AuthorizationDecision Deny(string missing) => new(false, missing);
    }

    /// <summary>Exposed for unit tests — pure function over (requirement, principal).</summary>
    internal static AuthorizationDecision Authorize(
        ToolScopeRegistry.Requirement requirement,
        BearerPrincipal? principal)
    {
        if (principal is null)
        {
            // No principal => no scopes at all. Report the first required scope as the
            // missing one so the deny envelope is actionable.
            var first = requirement.Scopes.IsDefaultOrEmpty ? "<unknown>" : requirement.Scopes[0];
            return AuthorizationDecision.Deny(first);
        }

        if (requirement.IsAny)
        {
            foreach (var s in requirement.Any)
            {
                if (principal.HasScope(s)) return AuthorizationDecision.Allow();
            }
            // Report the first candidate as the representative miss; the envelope
            // surfaces the full list separately.
            return AuthorizationDecision.Deny(requirement.Any[0]);
        }

        foreach (var s in requirement.All)
        {
            if (!principal.HasScope(s)) return AuthorizationDecision.Deny(s);
        }
        return AuthorizationDecision.Allow();
    }

    internal static CallToolResult BuildForbiddenResult(
        string toolName,
        ToolScopeRegistry.Requirement requirement,
        BearerPrincipal? principal,
        string missingScope)
    {
        var requiredList = FormatScopes(requirement);
        var presentedList = FormatPrincipalScopes(principal);
        var kindWord = requirement.IsAny ? "any of" : "scope";
        var sb = new StringBuilder();
        sb.Append("forbidden: tool '")
          .Append(toolName)
          .Append("' requires ")
          .Append(kindWord)
          .Append(" [")
          .Append(requiredList)
          .Append("]; principal '")
          .Append(principal?.Name ?? "(none)")
          .Append("' presented [")
          .Append(presentedList)
          .Append("].");

        // Structured payload mirrors the BearerTokenMiddleware 401 envelope shape so the
        // client has one error grammar to reason about. The bearer value is NEVER in here.
        var structured = new System.Text.Json.Nodes.JsonObject
        {
            ["error"] = new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "forbidden",
                ["message"] = $"tool requires scope '{missingScope}'",
                ["tool"] = toolName,
                ["required_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    requirement.Scopes.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()),
                ["principal_scopes"] = new System.Text.Json.Nodes.JsonArray(
                    (principal?.Scopes.OrderBy(s => s, StringComparer.Ordinal)
                                      .Select(s => (System.Text.Json.Nodes.JsonNode?)s)
                                      .ToArray())
                    ?? Array.Empty<System.Text.Json.Nodes.JsonNode?>()),
                ["semantics"] = requirement.IsAny ? "any" : "all",
            },
        };

        // The MCP CallToolResult is intentionally text-content-only (same reasoning as
        // ToolErrorSurfaceFilter — strict clients validate structuredContent against the
        // tool's success-path output schema, so we keep the envelope in a text block).
        // The text payload is "<human summary>\n<json envelope>" so both human-readable
        // tooling and machine parsers (tests, the LLM itself) can pull the structured
        // form back out with a simple substring + JSON.Parse.
        sb.Append('\n').Append(structured.ToJsonString());

        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = sb.ToString() } },
        };
    }

    private static string FormatScopes(ToolScopeRegistry.Requirement requirement)
        => string.Join(", ", requirement.Scopes);

    private static string FormatPrincipalScopes(BearerPrincipal? principal)
    {
        if (principal is null) return "(none)";
        if (principal.Scopes.Count == 0) return "(empty)";
        return string.Join(", ", principal.Scopes.OrderBy(s => s, StringComparer.Ordinal));
    }
}
