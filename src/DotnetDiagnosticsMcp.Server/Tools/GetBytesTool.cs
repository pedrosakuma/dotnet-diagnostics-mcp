using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 §4.4 successor for <c>get_module_bytes</c> and <c>get_dump_bytes</c>: a single
/// byte-fetch surface that dispatches on a <c>kind</c> discriminator. Both legacy tools
/// share the same <see cref="ByteFetchEnvelope"/>, chunking contract, and downstream
/// consumers (dotnet-assembly-mcp, dotnet-native-mcp, orchestrator proxy), so merging them
/// reduces the visible MCP surface without changing the wire shape.
/// </summary>
/// <remarks>
/// <para>Implementation delegates to the existing <see cref="DiagnosticTools.GetModuleBytes"/>
/// and <see cref="DiagnosticTools.GetDumpBytes"/> entrypoints so the legacy tools and
/// this successor stay byte-for-byte compatible (asserted by
/// <c>GetBytesCompatibilityTests</c>). When the legacy tools are eventually removed, the
/// implementations will move here and the call direction will flip.</para>
/// </remarks>
[McpServerToolType]
public sealed class GetBytesTool
{
    internal const string KindModule = "module";
    internal const string KindDump = "dump";

    internal static readonly IReadOnlyList<string> AllowedKinds = new[] { KindModule, KindDump };

    [RequireScope("module-bytes-read")]
    [McpServerTool(
        Name = "get_bytes",
        Title = "Fetch module PE/PDB or dump bytes as chunks",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Streams a managed module artifact (PE or PDB) or a dump file as repeated CallTool chunks so sibling MCPs can materialise pod-local binaries through the orchestrator proxy. " +
        "Dispatches on 'kind': 'module' (resolve by ModuleVersionId in a live process; asset defaults to 'pe'; optional processId — server auto-selects when omitted) or 'dump' (path under MCP_ARTIFACT_ROOT, re-validated every call). " +
        "maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB. " +
        "Successor to 'get_module_bytes' and 'get_dump_bytes' (RFC 0002 §4.4); both legacy tools remain available during the deprecation window and emit identical envelopes.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetBytes(
        IModuleByteSource moduleByteSource,
        IDumpByteSource dumpByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        [Description("Artifact kind to fetch: 'module' (PE/PDB of a loaded module) or 'dump' (dump file under the artifact root).")] string kind,
        [Description("Module MVID (GUID 'D' format). Required when kind='module'; ignored otherwise.")] string? moduleVersionId = null,
        [Description("Module artifact when kind='module': 'pe' (default) or 'pdb'. Ignored when kind='dump'.")] string asset = "pe",
        [Description("Dump path when kind='dump'. Relative paths resolve under MCP_ARTIFACT_ROOT; absolute paths must still resolve under that root. Ignored when kind='module'.")] string? dumpFilePath = null,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Operating system process id of the target .NET process. Used only when kind='module'; optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<ByteFetchEnvelope>(
                kind,
                AllowedKinds,
                nameof(kind),
                out var canonicalKind,
                out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        return canonicalKind switch
        {
            KindModule => await DiagnosticTools.GetModuleBytes(
                moduleByteSource,
                resolver,
                principalAccessor,
                moduleVersionId ?? string.Empty,
                asset,
                offset,
                maxBytes,
                processId,
                loggerFactory,
                cancellationToken).ConfigureAwait(false),
            KindDump => await DiagnosticTools.GetDumpBytes(
                dumpByteSource,
                principalAccessor,
                dumpFilePath ?? string.Empty,
                offset,
                maxBytes,
                loggerFactory,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"DiscriminatorDispatch returned an unexpected canonical kind '{canonicalKind}'."),
        };
    }
}
