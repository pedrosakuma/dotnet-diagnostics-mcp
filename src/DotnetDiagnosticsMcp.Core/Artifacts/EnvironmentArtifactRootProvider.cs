using System.Runtime.InteropServices;

namespace DotnetDiagnosticsMcp.Core.Artifacts;

/// <summary>
/// Default <see cref="IArtifactRootProvider"/>: honours <c>MCP_ARTIFACT_ROOT</c> and falls
/// back to <c>{TempPath}/dotnet-diagnostics-mcp</c>. Resolves to an absolute path once at
/// construction; later mutations of the environment variable do not affect the resolved
/// root (matches how other singleton services behave in the DI container).
/// </summary>
public sealed class EnvironmentArtifactRootProvider : IArtifactRootProvider
{
    /// <summary>Name of the environment variable consulted on construction.</summary>
    public const string EnvironmentVariableName = "MCP_ARTIFACT_ROOT";

    public EnvironmentArtifactRootProvider()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp")
            : configured;

        Root = Path.GetFullPath(root);
        EnsureDirectoryWithRestrictivePermissions(Root);
    }

    public string Root { get; }

    private static void EnsureDirectoryWithRestrictivePermissions(string path)
    {
        Directory.CreateDirectory(path);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // POSIX: u=rwx (0700). Best-effort: failures are non-fatal (e.g. a mounted
            // PVC that already has stricter perms).
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }
}
