using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class EtwNativeThreadSnapshotInspectorTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var inspector = new EtwNativeThreadSnapshotInspector();
        inspector.IsAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task InspectLiveAsync_ThrowsUnauthorizedAccess_WhenEtwPrivilegesAreUnavailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            return;
        }

        var inspector = new EtwNativeThreadSnapshotInspector();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => inspector.InspectLiveAsync(processId: Environment.ProcessId, options: null, cancellationToken: CancellationToken.None));
    }
}
