namespace DotnetDiagnosticsMcp.Core;

/// <summary>
/// Thrown when a host-side external dependency required by a diagnostic path (for example
/// <c>eu-stack</c> or <c>perf</c>) is missing from the execution environment.
/// </summary>
public sealed class ExternalToolNotFoundException : InvalidOperationException
{
    public ExternalToolNotFoundException(string toolName, string message)
        : base(message)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }
}
