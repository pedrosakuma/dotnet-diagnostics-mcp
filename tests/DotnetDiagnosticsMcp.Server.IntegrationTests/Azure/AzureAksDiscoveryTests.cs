using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using global::Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Adapter-faked unit tests for the real <see cref="AzureAksDiscovery"/> backend
/// (#234). Locks in: readinessWarnings (private cluster, agentPool=0, 403 on
/// listClusterUserCredential), no partial handoff on 403, kubeconfig handle
/// minting when includeKubeconfig=true, synthetic cursor across pages.
/// </summary>
public sealed class AzureAksDiscoveryTests
{
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";

    private static (AzureAksDiscovery Discovery, InMemoryKubeconfigHandleStore Store, FakeAdapter Adapter) Build(
        IEnumerable<AzureAksClusterRow> rows,
        Func<string, byte[]>? credResolver = null,
        Func<string, Exception?>? credFailure = null)
    {
        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) },
            clock);
        var adapter = new FakeAdapter(rows.ToArray(), credResolver, credFailure);
        var discovery = new AzureAksDiscovery(adapter, store, NullLogger<AzureAksDiscovery>.Instance);
        return (discovery, store, adapter);
    }

    [Fact]
    public async Task ListAsync_PrivateCluster_EmitsReadinessWarning_AndNullsFqdn()
    {
        var (sut, _, _) = Build(new[]
        {
            new AzureAksClusterRow(
                ResourceId: "/subscriptions/x/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/private-aks",
                Name: "private-aks", Location: "westeurope",
                AgentPoolCount: 2, Fqdn: null, KubernetesVersion: "1.30.0",
                NodeResourceGroup: "MC_rg_private-aks_westeurope", IsPrivateCluster: true),
        });

        var result = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 100, null, false), CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].ReadinessWarnings.Should().ContainSingle().Which
            .Should().Contain("private cluster", "private clusters need VNet integration to reach the API server");
        result.Items[0].Fqdn.Should().BeNull();
        result.Items[0].Handoff.Should().BeNull("includeKubeconfig=false must never mint a handle");
    }

    [Fact]
    public async Task ListAsync_AgentPoolCountZero_EmitsReadinessWarning()
    {
        var (sut, _, _) = Build(new[]
        {
            new AzureAksClusterRow(
                ResourceId: "/subscriptions/x/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/empty",
                Name: "empty", Location: "eastus",
                AgentPoolCount: 0, Fqdn: "empty-abc.hcp.eastus.azmk8s.io",
                KubernetesVersion: "1.29.4", NodeResourceGroup: "MC_rg_empty_eastus",
                IsPrivateCluster: false),
        });

        var result = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 100, null, false), CancellationToken.None);

        result.Items[0].ReadinessWarnings.Should().ContainSingle().Which
            .Should().Contain("agent pool count == 0");
    }

    [Fact]
    public async Task ListAsync_IncludeKubeconfigTrue_MintsHandle_AndStoresBytes()
    {
        var rows = new[]
        {
            Row("aks-prod", "westeurope"),
        };
        var (sut, store, _) = Build(rows, credResolver: _ => Encoding.UTF8.GetBytes("apiVersion: v1\nkind: Config\nusers: []"));

        var result = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 100, null, IncludeKubeconfig: true),
            CancellationToken.None);

        result.Items.Should().HaveCount(1);
        var handoff = result.Items[0].Handoff;
        handoff.Should().NotBeNull();
        handoff!.KubeconfigHandle.Should().StartWith("kc:");
        handoff.ExpiresAt.Should().BeAfter(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The store actually contains the kubeconfig bytes the handle resolves to.
        var bytes = store.TryResolve(handoff.KubeconfigHandle);
        bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(bytes!).Should().StartWith("apiVersion: v1");
    }

    [Fact]
    public async Task ListAsync_403OnListClusterUserCredential_EmitsWarning_AndNullsHandoff()
    {
        var rows = new[] { Row("forbidden-aks", "northeurope") };
        var (sut, store, _) = Build(rows,
            credFailure: _ => new RequestFailedException(status: (int)HttpStatusCode.Forbidden, message: "denied"));

        var result = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 100, null, IncludeKubeconfig: true),
            CancellationToken.None);

        var item = result.Items.Single();
        item.Handoff.Should().BeNull("a 403 must NEVER leak a partial handle");
        item.ReadinessWarnings.Should().ContainSingle().Which
            .Should().Contain("no AKS Cluster User permission detected");
        store.Count.Should().Be(0, "no handle was minted, so the store stays empty");
    }

    [Fact]
    public async Task ListAsync_OtherFailureOnCredentials_EmitsSanitizedWarning_AndStillReturnsCluster()
    {
        var rows = new[] { Row("flaky-aks", "westus2") };
        var (sut, _, _) = Build(rows,
            credFailure: _ => new InvalidOperationException("\u001b[31mAnsiInjection\u001b[0m"));

        var result = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 100, null, IncludeKubeconfig: true),
            CancellationToken.None);

        var item = result.Items.Single();
        item.Handoff.Should().BeNull();
        item.ReadinessWarnings.Should().ContainSingle().Which.Should().Be("failed to fetch kubeconfig: InvalidOperationException");
    }

    [Fact]
    public async Task ListAsync_SyntheticCursor_PagesAcrossMultipleCalls()
    {
        var rows = Enumerable.Range(0, 7).Select(i => Row("aks-" + i, "eastus")).ToArray();
        var (sut, _, _) = Build(rows);

        var page1 = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 3, null, false), CancellationToken.None);
        page1.Items.Should().HaveCount(3);
        page1.NextCursor.Should().NotBeNull();
        page1.NextCursor!.Should().StartWith("off:");

        var page2 = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 3, page1.NextCursor, false), CancellationToken.None);
        page2.Items.Should().HaveCount(3);
        page2.NextCursor.Should().NotBeNull();

        var page3 = await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, null, false, 3, page2.NextCursor, false), CancellationToken.None);
        page3.Items.Should().HaveCount(1);
        page3.NextCursor.Should().BeNull("no more pages after the last partial page");

        var allNames = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(c => c.Name).ToArray();
        allNames.Should().BeEquivalentTo(rows.Select(r => r.Name), o => o.WithStrictOrdering());
    }

    private static AzureAksClusterRow Row(string name, string location) => new(
        ResourceId: $"/subscriptions/x/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/{name}",
        Name: name,
        Location: location,
        AgentPoolCount: 1,
        Fqdn: $"{name}-x.hcp.{location}.azmk8s.io",
        KubernetesVersion: "1.30.0",
        NodeResourceGroup: $"MC_rg_{name}_{location}",
        IsPrivateCluster: false);

    private sealed class FakeAdapter : IAzureManagedClusterCollectionAdapter
    {
        private readonly AzureAksClusterRow[] _rows;
        private readonly Func<string, byte[]>? _resolver;
        private readonly Func<string, Exception?>? _failure;

        public FakeAdapter(
            AzureAksClusterRow[] rows,
            Func<string, byte[]>? resolver,
            Func<string, Exception?>? failure)
        {
            _rows = rows;
            _resolver = resolver;
            _failure = failure;
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AzureAksClusterRow> ListAsync(
            string subscriptionId,
            string? resourceGroup,
            [EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }

        public Task<byte[]> GetClusterUserKubeconfigAsync(string resourceId, CancellationToken cancellationToken)
        {
            var failure = _failure?.Invoke(resourceId);
            if (failure is not null) throw failure;
            var resolved = _resolver?.Invoke(resourceId) ?? Encoding.UTF8.GetBytes("placeholder");
            return Task.FromResult(resolved);
        }
    }

    private sealed class ControllableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
