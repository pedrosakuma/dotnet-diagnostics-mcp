using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Artifacts;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Test-only <see cref="IArtifactRootProvider"/> that pins the root to a caller-supplied
/// directory. Mirrors the production provider's "ensure dir + restrict perms" contract so
/// tests exercise the same code path the real sidecar would.
/// </summary>
internal sealed class TestArtifactRootProvider : IArtifactRootProvider
{
    public TestArtifactRootProvider(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
            }
        }
    }

    public string Root { get; }
}
