using System.Collections.Immutable;
using System.Reflection;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Static map from MCP tool name (the value of <c>[McpServerTool].Name</c>) to the
/// scope requirement declared by <see cref="RequireScopeAttribute"/> /
/// <see cref="RequireAnyScopeAttribute"/>. Built once at startup by scanning the
/// supplied tool surface types via reflection.
/// </summary>
/// <remarks>
/// <para>The MCP SDK's <c>CallToolFilter</c> pipeline only sees the request by name; we
/// need the attribute lookup to be O(1) per call. Building the index lazily inside the
/// filter would also force every test scenario to re-scan, so we centralise it here.</para>
/// <para>The constructor enforces the RFC §2.14 coverage rule: every static method that
/// carries a <c>[McpServerTool]</c> attribute on any scanned type <i>must</i> declare a
/// scope. A missing scope is a startup error rather than a per-call surprise — silently
/// allowing an undecorated tool would re-introduce the "single bearer == root"
/// regression this RFC exists to close.</para>
/// </remarks>
internal sealed class ToolScopeRegistry
{
    /// <summary>Resolved requirement for a tool. Exactly one of <see cref="All"/> /
    /// <see cref="Any"/> is non-empty.</summary>
    /// <param name="All">Scopes the principal must hold every one of (AND semantics).</param>
    /// <param name="Any">Scopes of which the principal must hold at least one (OR semantics).</param>
    public readonly record struct Requirement(
        ImmutableArray<string> All,
        ImmutableArray<string> Any)
    {
        public bool IsAny => !Any.IsDefault && Any.Length > 0;
        public ImmutableArray<string> Scopes => IsAny ? Any : All;
    }

    private readonly ImmutableDictionary<string, Requirement> _byToolName;

    private ToolScopeRegistry(ImmutableDictionary<string, Requirement> byToolName)
    {
        _byToolName = byToolName;
    }

    /// <summary>Returns the requirement registered for <paramref name="toolName"/>, or
    /// <c>null</c> when the tool is not part of any scanned surface. Unregistered tools
    /// must be treated as deny by the caller (defense-in-depth: an attribute-less
    /// drilldown should never become a covert wildcard).</summary>
    public Requirement? TryGet(string toolName)
        => _byToolName.TryGetValue(toolName, out var req) ? req : null;

    /// <summary>Tool names registered with this index (used by tests / startup logs).</summary>
    public IReadOnlyCollection<string> KnownToolNames => _byToolName.Keys.ToArray();

    /// <summary>Scans the supplied tool surface types for <c>[McpServerTool]</c> methods
    /// and reads their scope attributes. Throws when any tool method is missing both
    /// <see cref="RequireScopeAttribute"/> and <see cref="RequireAnyScopeAttribute"/>,
    /// or when both are present on the same method (the latter is a programming error —
    /// AND-vs-OR must be a single choice per tool).</summary>
    public static ToolScopeRegistry Build(IEnumerable<Type> toolSurfaceTypes)
    {
        ArgumentNullException.ThrowIfNull(toolSurfaceTypes);

        var builder = ImmutableDictionary.CreateBuilder<string, Requirement>(StringComparer.Ordinal);
        var missing = new List<string>();
        var conflicts = new List<string>();

        foreach (var type in toolSurfaceTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var toolName = toolAttr.Name;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    // Defensive: every tool in this server declares Name explicitly. If a
                    // future addition forgets, fail loudly instead of silently keying off
                    // the method name.
                    throw new InvalidOperationException(
                        $"[McpServerTool] on {type.FullName}.{method.Name} must specify a Name.");
                }

                var require = method.GetCustomAttribute<RequireScopeAttribute>();
                var requireAny = method.GetCustomAttribute<RequireAnyScopeAttribute>();
                if (require is not null && requireAny is not null)
                {
                    conflicts.Add($"{type.FullName}.{method.Name} (tool '{toolName}')");
                    continue;
                }

                if (require is null && requireAny is null)
                {
                    missing.Add($"{type.FullName}.{method.Name} (tool '{toolName}')");
                    continue;
                }

                var req = require is not null
                    ? new Requirement(All: require.Scopes, Any: ImmutableArray<string>.Empty)
                    : new Requirement(All: ImmutableArray<string>.Empty, Any: requireAny!.Scopes);

                builder[toolName] = req;
            }
        }

        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "These tool methods carry both [RequireScope] and [RequireAnyScope]; pick one: " +
                string.Join(", ", conflicts));
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Every [McpServerTool] must declare a scope via [RequireScope] or [RequireAnyScope] " +
                "(RFC 0001 §2 / sub-issue B5.2). Missing: " + string.Join(", ", missing));
        }

        return new ToolScopeRegistry(builder.ToImmutable());
    }
}
