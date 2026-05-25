namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Stable <see cref="DotnetDiagnosticsMcp.Core.DiagnosticError.Kind"/> values surfaced by
/// the Azure discovery tool (#232). Constants — not an enum — so the tool can write them
/// verbatim into the structured error envelope.
/// </summary>
public static class AzureDiscoveryErrorKinds
{
    public const string InvalidArgument = "InvalidArgument";
    public const string PermissionDenied = "PermissionDenied";
    public const string AzureDiscoveryDisabled = "AzureDiscoveryDisabled";
}
