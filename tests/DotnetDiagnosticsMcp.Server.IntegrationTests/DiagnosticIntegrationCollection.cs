using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticIntegrationGroup
{
    public const string Name = "DiagnosticIntegration";
}
