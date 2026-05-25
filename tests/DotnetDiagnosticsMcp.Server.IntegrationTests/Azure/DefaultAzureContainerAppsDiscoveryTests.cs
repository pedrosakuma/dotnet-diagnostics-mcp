using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Unit tests for <see cref="DefaultAzureContainerAppsDiscovery"/> using an
/// in-memory <see cref="IAzureContainerAppCollectionAdapter"/> fake.
/// </summary>
public sealed class DefaultAzureContainerAppsDiscoveryTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task ListAsync_MapsTwoContainerSidecarTopology_NoWarnings()
    {
        var snapshot = new AzureContainerAppSnapshot(
            ResourceId: "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/containerApps/checkout",
            Name: "checkout",
            Location: "westeurope",
            ContainerImages: new[] { "registry.io/app:1", "registry.io/diag:1" },
            ProvisioningState: "Succeeded",
            RunningState: "Running",
            LatestRevisionFqdn: "checkout.example.io",
            MinReplicas: 1,
            MaxReplicas: 5,
            ContainerCount: 2);
        var adapter = new FakeContainerAppsAdapter(new[] { Page(new[] { snapshot }, nextToken: null) });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        var item = page.Items.Should().ContainSingle().Subject;
        item.Name.Should().Be("checkout");
        item.ContainerImages.Should().BeEquivalentTo(new[] { "registry.io/app:1", "registry.io/diag:1" });
        item.MinReplicas.Should().Be(1);
        item.MaxReplicas.Should().Be(5);
        item.LatestRevisionFqdn.Should().Be("checkout.example.io");
        item.ReadinessWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_SingleContainerApp_EmitsSidecarTopologyWarning()
    {
        var snapshot = SingleContainerApp("app1", minReplicas: 1);
        var adapter = new FakeContainerAppsAdapter(new[] { Page(new[] { snapshot }, nextToken: null) });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Should().ContainSingle()
            .Which.ReadinessWarnings.Should()
                .ContainSingle("No second container detected — sidecar topology not deployed");
    }

    [Fact]
    public async Task ListAsync_MinReplicasZero_EmitsScaleZeroWarning()
    {
        var snapshot = new AzureContainerAppSnapshot(
            ResourceId: "/x/containerApps/scale-zero",
            Name: "scale-zero",
            Location: "westeurope",
            ContainerImages: new[] { "registry.io/app:1", "registry.io/diag:1" },
            ProvisioningState: "Succeeded",
            RunningState: "Running",
            LatestRevisionFqdn: "scale-zero.example.io",
            MinReplicas: 0,
            MaxReplicas: 5,
            ContainerCount: 2);
        var adapter = new FakeContainerAppsAdapter(new[] { Page(new[] { snapshot }, nextToken: null) });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Should().ContainSingle()
            .Which.ReadinessWarnings.Should().Contain("Scale=0");
    }

    [Fact]
    public async Task ListAsync_IncludeStoppedFalse_FiltersStopped()
    {
        var running = TwoContainerApp("running", "Running", minReplicas: 1);
        var stopped = TwoContainerApp("stopped", "Stopped", minReplicas: 1);
        var adapter = new FakeContainerAppsAdapter(new[] { Page(new[] { running, stopped }, nextToken: null) });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(includeStopped: false), CancellationToken.None);

        page.Items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "running" });
    }

    [Fact]
    public async Task ListAsync_ResourceGroupFilter_PassedToAdapter()
    {
        var adapter = new FakeContainerAppsAdapter(new[] { Page(Array.Empty<AzureContainerAppSnapshot>(), null) });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        await sut.ListAsync(
            new AzureDiscoveryRequest(SubscriptionId, "rg-prod", false, 50, "cursor-A", false),
            CancellationToken.None);

        adapter.LastCall.Should().NotBeNull();
        adapter.LastCall!.Value.ResourceGroup.Should().Be("rg-prod");
        adapter.LastCall.Value.PageSize.Should().Be(50);
        adapter.LastCall.Value.ContinuationToken.Should().Be("cursor-A");
    }

    [Fact]
    public async Task ListAsync_MultiPageAdapter_ReturnsFirstPageCursorAndStopsThere()
    {
        var p1 = Page(new[] { TwoContainerApp("a"), TwoContainerApp("b") }, nextToken: "page-2");
        var p2 = Page(new[] { TwoContainerApp("c") }, nextToken: null);
        var adapter = new FakeContainerAppsAdapter(new[] { p1, p2 });
        var sut = new DefaultAzureContainerAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "a", "b" });
        page.NextCursor.Should().Be("page-2");
        adapter.PagesEnumerated.Should().Be(1);
    }

    // --- helpers -----------------------------------------------------------

    private static AzureDiscoveryRequest Request(bool includeStopped = false) =>
        new(SubscriptionId, null, includeStopped, 100, null, false);

    private static AzurePage<AzureContainerAppSnapshot> Page(
        IReadOnlyList<AzureContainerAppSnapshot> items, string? nextToken) =>
        new(items, nextToken);

    private static AzureContainerAppSnapshot SingleContainerApp(string name, int minReplicas) => new(
        ResourceId: $"/x/containerApps/{name}",
        Name: name,
        Location: "westeurope",
        ContainerImages: new[] { "registry.io/app:1" },
        ProvisioningState: "Succeeded",
        RunningState: "Running",
        LatestRevisionFqdn: $"{name}.example.io",
        MinReplicas: minReplicas,
        MaxReplicas: 3,
        ContainerCount: 1);

    private static AzureContainerAppSnapshot TwoContainerApp(string name, string runningState = "Running", int minReplicas = 1) => new(
        ResourceId: $"/x/containerApps/{name}",
        Name: name,
        Location: "westeurope",
        ContainerImages: new[] { "registry.io/app:1", "registry.io/diag:1" },
        ProvisioningState: "Succeeded",
        RunningState: runningState,
        LatestRevisionFqdn: $"{name}.example.io",
        MinReplicas: minReplicas,
        MaxReplicas: 3,
        ContainerCount: 2);

    private sealed class FakeContainerAppsAdapter : IAzureContainerAppCollectionAdapter
    {
        private readonly IReadOnlyList<AzurePage<AzureContainerAppSnapshot>> _pages;
        public (string SubscriptionId, string? ResourceGroup, int PageSize, string? ContinuationToken)? LastCall { get; private set; }
        public int PagesEnumerated { get; private set; }

        public FakeContainerAppsAdapter(IReadOnlyList<AzurePage<AzureContainerAppSnapshot>> pages)
        {
            _pages = pages;
        }

        public async IAsyncEnumerable<AzurePage<AzureContainerAppSnapshot>> GetContainerAppsAsPagesAsync(
            string subscriptionId,
            string? resourceGroup,
            int pageSize,
            string? continuationToken,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastCall = (subscriptionId, resourceGroup, pageSize, continuationToken);
            foreach (var page in _pages)
            {
                PagesEnumerated++;
                yield return page;
                await Task.Yield();
            }
        }
    }
}
