using System;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// xunit <see cref="FactAttribute"/> variant that runtime-skips a test only when
/// running on Linux CI (<c>CI=true</c> + <see cref="OperatingSystem.IsLinux"/>).
/// Local Linux developers, Windows CI, and macOS CI still execute the test.
/// </summary>
/// <remarks>
/// Used to quarantine `LiveCoreClrProcessTests` cases that reliably segfault the
/// xunit test host on ubuntu-latest under full-suite load (native crash inside
/// libcoreclr's EventPipe SampleProfiler — see issues #145 and #147). The skip
/// is conditional so the regression tests stay runnable locally and on Windows,
/// preserving coverage of the closed-generic handoff contract from issue #21.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SkipOnLinuxCiFactAttribute : FactAttribute
{
    public SkipOnLinuxCiFactAttribute(string skipReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skipReason);

        if (IsLinuxCi())
        {
            Skip = skipReason;
        }
    }

    private static bool IsLinuxCi()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var ci = Environment.GetEnvironmentVariable("CI");
        return string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ci, "1", StringComparison.Ordinal);
    }
}
