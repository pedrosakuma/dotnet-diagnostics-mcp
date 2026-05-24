using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.7 / issue #212 — asserts that <c>list_orchestrator(kind=...)</c> returns the
/// same payload shape, summary, and hints as the legacy <c>list_pods</c> /
/// <c>list_active_investigations</c> tools. Uses the shared
/// <see cref="CompatibilityEnvelopeAssert"/> scaffolding (#214) so the dual-entrypoint
/// guarantee is one short test pair per kind.
/// </summary>
public sealed class ListOrchestratorCompatibilityTests
{
    // ---- kind=pods ---------------------------------------------------------------------

    [Fact]
    public async Task Pods_LegacyAndKindPods_ReturnStructurallyIdenticalEnvelopes()
    {
        // Both legacy and successor calls hit the SAME IPodInventory and the same params,
        // so the underlying PodCandidatePage is byte-identical. The only re-frame is the
        // outer Data wrapper: successor wraps in ListOrchestratorResult.Pods.
        var inventory = new StubPodInventory(new PodCandidatePage(
            Items: new[]
            {
                new PodCandidate(
                    Namespace: "ns",
                    Name: "pod-1",
                    ContainerName: "api",
                    Phase: "Running",
                    Ready: true,
                    CreatedAt: new System.DateTimeOffset(2025, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
                    NodeName: "node-a",
                    OwnerKind: "Deployment",
                    OwnerName: "api",
                    ImageRef: "ghcr.io/x:1",
                    Labels: new Dictionary<string, string> { ["app"] = "api" },
                    DiagnosticsPrepared: true,
                    PreparationReason: "ok",
                    ActiveInvestigationCount: 0),
            },
            NextCursor: null));

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var legacy = await OrchestratorTools.ListPods(
                    inventory,
                    @namespace: "ns",
                    labelSelector: "app=api",
                    fieldSelector: null,
                    containerName: null,
                    preparedOnly: true,
                    includeNotReady: false,
                    limit: 50,
                    cursor: null,
                    cancellationToken: CancellationToken.None);
                return Strip(legacy.Summary, legacy.Hints, legacy.Error, legacy.Data);
            },
            successor: async () =>
            {
                var options = new OrchestratorOptions { Enabled = true };
                var successor = await ListOrchestratorTool.ListOrchestrator(
                    inventory,
                    store: new MemoryInvestigationStore(),
                    options,
                    TestPrincipalAccessors.Root,
                    server: null,
                    loggerFactory: null,
                    kind: ListOrchestratorTool.KindPods,
                    @namespace: "ns",
                    labelSelector: "app=api",
                    fieldSelector: null,
                    containerName: null,
                    preparedOnly: true,
                    includeNotReady: false,
                    limit: 50,
                    cursor: null,
                    includeTerminal: false,
                    includeAllSessions: false,
                    cancellationToken: CancellationToken.None);
                return Strip(successor.Summary, successor.Hints, successor.Error, successor.Data?.Pods);
            });
    }

    // ---- kind=investigations -----------------------------------------------------------

    [Fact]
    public async Task Investigations_LegacyAndKindInvestigations_ReturnStructurallyIdenticalEnvelopes()
    {
        var seedStoreLegacy = SeedStore();
        var seedStoreSuccessor = SeedStore();
        var options = new OrchestratorOptions { Enabled = true };

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var legacy = await OrchestratorTools.ListActiveInvestigations(
                    seedStoreLegacy,
                    options,
                    TestPrincipalAccessors.Root,
                    server: null!,
                    loggerFactory: null,
                    includeTerminal: false,
                    includeAllSessions: false);
                return Strip(legacy.Summary, legacy.Hints, legacy.Error, legacy.Data);
            },
            successor: async () =>
            {
                var successor = await ListOrchestratorTool.ListOrchestrator(
                    inventory: new ThrowingPodInventory(),
                    store: seedStoreSuccessor,
                    options,
                    TestPrincipalAccessors.Root,
                    server: null,
                    loggerFactory: null,
                    kind: ListOrchestratorTool.KindInvestigations,
                    includeTerminal: false,
                    includeAllSessions: false,
                    cancellationToken: CancellationToken.None);
                return Strip(successor.Summary, successor.Hints, successor.Error, successor.Data?.Investigations);
            });
    }

    // ---- per-kind scope tightening + discriminator validation -------------------------

    [Fact]
    public async Task KindInvestigations_RequiresOrchestratorAttachScope()
    {
        var options = new OrchestratorOptions { Enabled = true };
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: new ThrowingPodInventory(),
            store: new MemoryInvestigationStore(),
            options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-list"),
            kind: ListOrchestratorTool.KindInvestigations);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        result.Hints.Should().Contain(h => h.NextTool == "list_orchestrator");
    }

    [Fact]
    public async Task KindPods_AcceptsOrchestratorListScope()
    {
        var options = new OrchestratorOptions { Enabled = true };
        var inventory = new StubPodInventory(new PodCandidatePage(System.Array.Empty<PodCandidate>(), NextCursor: null));
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory,
            store: new MemoryInvestigationStore(),
            options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-list"),
            kind: ListOrchestratorTool.KindPods);

        result.IsError.Should().BeFalse();
        result.Data!.Kind.Should().Be("pods");
        result.Data.Pods.Should().NotBeNull();
        result.Data.Investigations.Should().BeNull();
    }

    [Fact]
    public async Task UnknownKind_ReturnsInvalidArgumentEnvelope()
    {
        var options = new OrchestratorOptions { Enabled = true };
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: new ThrowingPodInventory(),
            store: new MemoryInvestigationStore(),
            options,
            TestPrincipalAccessors.Root,
            kind: "everything");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("pods").And.Contain("investigations");
    }

    [Fact]
    public async Task OrchestratorDisabled_ReturnsStructuredFailure()
    {
        var options = new OrchestratorOptions { Enabled = false };
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: new ThrowingPodInventory(),
            store: new MemoryInvestigationStore(),
            options,
            TestPrincipalAccessors.Root,
            kind: ListOrchestratorTool.KindPods);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.OrchestratorDisabled);
    }

    // ---- helpers ----------------------------------------------------------------------

    private static MemoryInvestigationStore SeedStore()
    {
        var store = new MemoryInvestigationStore();
        store.Add(new InvestigationHandle(
            HandleId: "h-active",
            Namespace: "ns",
            PodName: "pod",
            TargetContainerName: "api",
            EphemeralContainerName: "diag",
            PodLocalBearerToken: "secret",
            State: InvestigationState.Active,
            AttachedAt: new System.DateTimeOffset(2025, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
            ExpiresAt: new System.DateTimeOffset(2025, 1, 1, 0, 30, 0, System.TimeSpan.Zero)));
        return store;
    }

    private sealed record Snapshot(string Summary, IReadOnlyList<NextActionHint> Hints, DiagnosticError? Error, object? Data);

    private static Snapshot Strip<T>(string summary, IReadOnlyList<NextActionHint> hints, DiagnosticError? error, T? data)
        => new(summary, hints, error, data);

    private sealed class StubPodInventory : IPodInventory
    {
        private readonly PodCandidatePage _page;
        public StubPodInventory(PodCandidatePage page) { _page = page; }
        public Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
            => Task.FromResult(_page);
    }

    private sealed class ThrowingPodInventory : IPodInventory
    {
        public Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
            => throw new System.InvalidOperationException("Pod inventory must not be touched for kind=investigations.");
    }
}
