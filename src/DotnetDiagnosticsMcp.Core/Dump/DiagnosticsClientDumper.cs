using System.Globalization;
using DotnetDiagnosticsMcp.Core.Artifacts;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Default <see cref="IProcessDumper"/> backed by <see cref="DiagnosticsClient.WriteDump(DumpType, string, bool)"/>.
/// </summary>
public sealed class DiagnosticsClientDumper : IProcessDumper
{
    private readonly IArtifactRootProvider _artifactRoot;
    private readonly ILogger<DiagnosticsClientDumper> _logger;

    public DiagnosticsClientDumper(
        IArtifactRootProvider artifactRoot,
        ILogger<DiagnosticsClientDumper>? logger = null)
    {
        _artifactRoot = artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot));
        _logger = logger ?? NullLogger<DiagnosticsClientDumper>.Instance;
    }

    public Task<DumpResult> WriteDumpAsync(
        int processId,
        ProcessDumpType dumpType,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // SafeArtifactPath rejects absolute paths and traversal/symlink escapes. The
        // default sub-path keeps the legacy on-disk layout when the caller passes null.
        var directory = SafeArtifactPath.ResolveDirectory(
            _artifactRoot.Root,
            outputDirectory,
            defaultRelative: ".",
            parameterName: nameof(outputDirectory));

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var fileName = $"dump_pid{processId.ToString(CultureInfo.InvariantCulture)}_{dumpType}_{stamp}.dmp";
        var fullPath = Path.Combine(directory, fileName);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new DiagnosticsClient(processId);
            var nativeType = (DumpType)(int)dumpType;
            _logger.LogInformation("Writing {DumpType} dump for pid {Pid} to {Path}", dumpType, processId, fullPath);

            // We can't pre-create the dump file (DiagnosticsClient.WriteDump fails
            // if the destination already exists). The umask / symlink-at-leaf race
            // is mitigated by:
            //   1. SafeArtifactPath.ResolveDirectory chmod's the artifact root tree
            //      to 0700 (owner-only). No other user can place a symlink inside
            //      a validated subtree.
            //   2. SetRestrictiveFilePermissions below now THROWS on POSIX chmod
            //      failure, so a dump that ends up with permissive bits is
            //      reported as a hard error instead of silently shipped.
            //   3. ResolveDirectory walks every path segment for symlinks before
            //      this point, so middle-segment escape is already blocked.
            client.WriteDump(nativeType, fullPath, logDumpGeneration: false);
            SafeArtifactPath.SetRestrictiveFilePermissions(fullPath);

            var info = new FileInfo(fullPath);
            return new DumpResult(
                ProcessId: processId,
                DumpType: dumpType,
                FilePath: fullPath,
                FileSizeBytes: info.Exists ? info.Length : 0,
                CreatedAt: DateTimeOffset.UtcNow);
        }, cancellationToken);
    }
}
