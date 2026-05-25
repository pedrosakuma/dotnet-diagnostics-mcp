using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

/// <summary>
/// Issue #202 — every test that calls
/// <c>OrchestratorAdminBypassPolicy.ResetWarningLatchForTests()</c> shares a
/// process-static one-shot warning latch. xUnit runs distinct test classes in
/// parallel by default, so two such tests racing in the same test host cause
/// either an extra warning (one trips the latch the other was about to assert)
/// or zero warnings (a sibling already burned the one shot). Pinning every
/// participating class to a single collection with parallelization disabled
/// serializes them and makes the latch reset deterministic. The latch is
/// intentionally process-wide in production (operators want exactly one
/// deprecation warning per process), so we keep the static and serialize the
/// tests instead of restructuring the policy class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LegacyAdminBypassLatchCollection
{
    public const string Name = "LegacyAdminBypassLatch";
}
