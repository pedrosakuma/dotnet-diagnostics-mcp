using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// End-to-end handoff between <c>discover_azure(kind=aksclusters, includeKubeconfig=true)</c>
/// and <c>list_orchestrator(kind=pods, kubeconfigHandle=...)</c> (#234).
/// </summary>
public sealed class DiscoverAzureToOrchestratorHandoffTests
{
    [Fact]
    public async Task ValidHandle_IsPushedOnContext_ForTheInnerListPodsCall_AndPoppedAfter()
    {
        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) },
            clock);
        var context = new AsyncLocalKubeconfigContext();

        var mint = store.Register(new byte[] { 0x61, 0x70, 0x69 });
        var inventory = new CapturingInventory(context);
        var options = new OrchestratorOptions { Enabled = true, DefaultNamespace = "diag" };
        options.NamespaceAllowlist.Add("diag");

        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: inventory,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.Root,
            kubeconfigContext: context,
            kubeconfigStore: store,
            kind: ListOrchestratorTool.KindPods,
            @namespace: "diag",
            kubeconfigHandle: mint.Handle,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse(result.Summary);
        inventory.HandleObservedDuringCall.Should().Be(mint.Handle,
            "the kubeconfigHandle MUST be active on the AsyncLocal context for the IPodInventory call");
        context.CurrentHandle.Should().BeNull("the handle MUST be popped after the tool call returns");
    }

    [Fact]
    public async Task UnknownHandle_ReturnsStructuredError_WithoutEchoingHandle()
    {
        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) },
            clock);
        var context = new AsyncLocalKubeconfigContext();

        var options = new OrchestratorOptions { Enabled = true, DefaultNamespace = "diag" };
        options.NamespaceAllowlist.Add("diag");

        const string fabricatedHandle = "kc:UNKNOWNHANDLEDOESNOTEXIST00000000";
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: new ThrowingInventory(),
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.Root,
            kubeconfigContext: context,
            kubeconfigStore: store,
            kind: ListOrchestratorTool.KindPods,
            @namespace: "diag",
            kubeconfigHandle: fabricatedHandle,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.KubeconfigHandleNotFound);
        result.Error.Message.Should().NotContain(fabricatedHandle,
            "the unresolved handle MUST NOT be echoed in any structured error field");
        result.Summary.Should().NotContain(fabricatedHandle);
        context.CurrentHandle.Should().BeNull("a failed handle resolution must not push a context");
    }

    [Fact]
    public async Task ExpiredHandle_ReturnsStructuredError_AndKubeconfigBytesAreZeroed()
    {
        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(1) },
            clock);
        var context = new AsyncLocalKubeconfigContext();

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var observable = payload;
        var mint = store.Register(payload);

        clock.Advance(TimeSpan.FromMinutes(2));

        var options = new OrchestratorOptions { Enabled = true, DefaultNamespace = "diag" };
        options.NamespaceAllowlist.Add("diag");

        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: new ThrowingInventory(),
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.Root,
            kubeconfigContext: context,
            kubeconfigStore: store,
            kind: ListOrchestratorTool.KindPods,
            @namespace: "diag",
            kubeconfigHandle: mint.Handle,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.KubeconfigHandleNotFound);

        observable.Should().OnlyContain(b => b == 0,
            because: "TryResolve on an expired entry MUST Array.Clear the underlying buffer before removing it.");
        store.Count.Should().Be(0);
    }

    private sealed class CapturingInventory : IPodInventory
    {
        private readonly IKubeconfigContext _context;
        public string? HandleObservedDuringCall { get; private set; }

        public CapturingInventory(IKubeconfigContext context) { _context = context; }

        public Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
        {
            HandleObservedDuringCall = _context.CurrentHandle;
            return Task.FromResult(new PodCandidatePage(Array.Empty<PodCandidate>(), null));
        }
    }

    private sealed class ThrowingInventory : IPodInventory
    {
        public Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("inventory must not be invoked when the handle is rejected up front");
    }

    private sealed class ControllableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
