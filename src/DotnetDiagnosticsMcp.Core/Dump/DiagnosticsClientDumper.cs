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

            // Pre-create the destination as an empty 0600 file. POSIX open(2) with
            // O_CREAT|O_TRUNC|O_WRONLY (which is what DiagnosticsClient.WriteDump uses
            // under the hood) IGNORES the mode argument when the file already exists,
            // so the dump payload inherits the pre-set 0600 mode. This closes the
            // umask-race window the security audit flagged. FileMode.CreateNew also
            // refuses to follow a symlink at the leaf (the symlink target would
            // already exist), defending against a TOCTOU swap between
            // SafeArtifactPath.ResolveDirectory and the write.
            using (SafeArtifactPath.CreateRestrictedFile(fullPath))
            {
            }

            client.WriteDump(nativeType, fullPath, logDumpGeneration: false);

            // Belt-and-braces: re-assert the mode and fail hard if the FS rejected it.
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
