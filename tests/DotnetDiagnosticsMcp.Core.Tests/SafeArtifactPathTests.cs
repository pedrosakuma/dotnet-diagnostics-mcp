using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Artifacts;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class SafeArtifactPathTests : IDisposable
{
    private readonly string _root;

    public SafeArtifactPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"diagmcp-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveDirectory_RejectsAbsolutePath()
    {
        var absolute = OperatingSystem.IsWindows() ? "C:\\Windows" : "/etc";

        var act = () => SafeArtifactPath.ResolveDirectory(_root, absolute, "default");

        act.Should().Throw<ArtifactPathException>()
            .Which.Reason.Should().Contain("absolute");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("sub/../../escape")]
    [InlineData("a/b/../../../etc")]
    public void ResolveDirectory_RejectsTraversal(string relative)
    {
        var act = () => SafeArtifactPath.ResolveDirectory(_root, relative, "default");

        act.Should().Throw<ArtifactPathException>()
            .Which.Reason.Should().Contain("..");
    }

    [Fact]
    public void ResolveDirectory_AcceptsValidRelativePath_AndCreatesDirectory()
    {
        var sub = Path.Combine("region", "alpha");
        var resolved = SafeArtifactPath.ResolveDirectory(_root, sub, "default");

        Directory.Exists(resolved).Should().BeTrue();
        resolved.Should().StartWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void ResolveDirectory_FallsBackToDefaultWhenRequestedIsNull()
    {
        var resolved = SafeArtifactPath.ResolveDirectory(_root, requestedRelative: null, "fallback");

        Directory.Exists(resolved).Should().BeTrue();
        resolved.Should().EndWith("fallback");
    }

    [Fact]
    public void ResolveDirectory_RejectsSymlinkEscape()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Creating a symlink on Windows typically requires SeCreateSymbolicLinkPrivilege;
            // skip rather than running an unreliable test.
            return;
        }

        var escapeTarget = Path.Combine(Path.GetTempPath(), $"diagmcp-escape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(escapeTarget);
        var linkName = Path.Combine(_root, "evil-link");
        try
        {
            Directory.CreateSymbolicLink(linkName, escapeTarget);

            var act = () => SafeArtifactPath.ResolveDirectory(_root, "evil-link/data", "default");

            act.Should().Throw<ArtifactPathException>()
                .Which.Reason.Should().Contain("escape");
        }
        finally
        {
            try { Directory.Delete(linkName); } catch { /* best-effort */ }
            try { Directory.Delete(escapeTarget, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SetRestrictiveFilePermissions_AppliesUserOnlyMode_OnPosix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var file = Path.Combine(_root, "artifact.bin");
        File.WriteAllBytes(file, new byte[] { 0x42 });

        SafeArtifactPath.SetRestrictiveFilePermissions(file);

        var mode = File.GetUnixFileMode(file);
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "the helper must collapse the file mode to user-only 0600");
    }

    [Fact]
    public void CreateRestrictedFile_BirthsFileWith0600_OnPosix()
    {
        var file = Path.Combine(_root, "born-restricted.bin");
        using (var fs = SafeArtifactPath.CreateRestrictedFile(file))
        {
            fs.Write(new byte[] { 0x42 }, 0, 1);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.GetUnixFileMode(file).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "FileStreamOptions.UnixCreateMode must apply 0600 at creation time, eliminating the umask race");
        }
    }

    [Fact]
    public void CreateRestrictedFile_RefusesPreExistingLeaf()
    {
        var file = Path.Combine(_root, "already-there.bin");
        File.WriteAllBytes(file, Array.Empty<byte>());

        var act = () =>
        {
            using var fs = SafeArtifactPath.CreateRestrictedFile(file);
        };

        // FileMode.CreateNew throws IOException when the leaf already exists, which
        // also defends against a symlink-swap attack between sandbox validation and
        // the write.
        act.Should().Throw<IOException>();
    }
}
