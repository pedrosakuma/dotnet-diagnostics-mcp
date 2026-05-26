using System;

namespace DotnetDiagnosticsMcp.Core.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string skipReason = "Linux-only test.")
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = skipReason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string skipReason = "Windows-only test.")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = skipReason;
        }
    }
}
