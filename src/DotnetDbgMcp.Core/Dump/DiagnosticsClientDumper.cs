using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.Dump;

/// <summary>
/// Default <see cref="IProcessDumper"/> backed by <see cref="DiagnosticsClient.WriteDump(DumpType, string, bool)"/>.
/// </summary>
public sealed class DiagnosticsClientDumper : IProcessDumper
{
    private readonly ILogger<DiagnosticsClientDumper> _logger;

    public DiagnosticsClientDumper(ILogger<DiagnosticsClientDumper>? logger = null)
    {
        _logger = logger ?? NullLogger<DiagnosticsClientDumper>.Instance;
    }

    public Task<DumpResult> WriteDumpAsync(
        int processId,
        ProcessDumpType dumpType,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "dotnet-dbg-mcp")
            : outputDirectory;

        Directory.CreateDirectory(directory);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var fileName = $"dump_pid{processId.ToString(CultureInfo.InvariantCulture)}_{dumpType}_{stamp}.dmp";
        var fullPath = Path.Combine(directory, fileName);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new DiagnosticsClient(processId);
            var nativeType = (DumpType)(int)dumpType;
            _logger.LogInformation("Writing {DumpType} dump for pid {Pid} to {Path}", dumpType, processId, fullPath);
            client.WriteDump(nativeType, fullPath, logDumpGeneration: false);

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
