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
/// Unit tests for <see cref="DefaultAzureWebAppsDiscovery"/> using an in-memory
/// <see cref="IAzureWebSiteCollectionAdapter"/> fake. Verifies cursor round-trip,
/// the resource-group filter, <c>includeStopped</c>, readiness-warning emission,
/// and function-app exclusion (Q1 in the design discussion).
/// </summary>
public sealed class DefaultAzureWebAppsDiscoveryTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task ListAsync_MapsRunningLinuxSiteWithoutWarnings()
    {
        var snapshot = new AzureWebSiteSnapshot(
            ResourceId: "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/foo",
            Name: "foo",
            Location: "westeurope",
            State: "Running",
            Kind: "app,linux",
            DefaultHostName: "foo.azurewebsites.net",
            LinuxFxVersion: "DOTNETCORE|8.0",
            NetFrameworkVersion: null,
            NumberOfWorkers: 3);
        var adapter = new FakeWebSitesAdapter(new[] { Page(new[] { snapshot }, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Should().HaveCount(1);
        var item = page.Items[0];
        item.ResourceId.Should().Be(snapshot.ResourceId);
        item.Name.Should().Be("foo");
        item.Location.Should().Be("westeurope");
        item.State.Should().Be("Running");
        item.Kind.Should().Be("app,linux");
        item.DefaultHostName.Should().Be("foo.azurewebsites.net");
        item.RuntimeStack.Should().Be("DOTNETCORE|8.0");
        item.RuntimeVersion.Should().Be("8.0");
        item.InstanceCount.Should().Be(3);
        item.ReadinessWarnings.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_WindowsSite_EmitsSidecarUnsupportedWarning()
    {
        var snapshot = WindowsSite("foo");
        var adapter = new FakeWebSitesAdapter(new[] { Page(new[] { snapshot }, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Should().ContainSingle()
            .Which.ReadinessWarnings.Should().ContainSingle("Windows OS — sidecar not supported");
    }

    [Fact]
    public async Task ListAsync_FunctionApp_IsExcluded()
    {
        // Both Linux Functions ("functionapp,linux") and Windows Functions ("functionapp") must be filtered.
        var snapshots = new[]
        {
            LinuxSite("normal-app"),
            new AzureWebSiteSnapshot("/x/sites/fn-linux", "fn-linux", "westeurope", "Running", "functionapp,linux", null, null, null, null),
            new AzureWebSiteSnapshot("/x/sites/fn-win",   "fn-win",   "westeurope", "Running", "functionapp",       null, null, null, null),
            LinuxSite("another-app"),
        };
        var adapter = new FakeWebSitesAdapter(new[] { Page(snapshots, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "normal-app", "another-app" });
    }

    [Fact]
    public async Task ListAsync_IncludeStoppedFalse_FiltersStopped()
    {
        var snapshots = new[]
        {
            LinuxSite("running-app", state: "Running"),
            LinuxSite("stopped-app", state: "Stopped"),
        };
        var adapter = new FakeWebSitesAdapter(new[] { Page(snapshots, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(includeStopped: false), CancellationToken.None);

        page.Items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "running-app" });
    }

    [Fact]
    public async Task ListAsync_IncludeStoppedTrue_KeepsStopped()
    {
        var snapshots = new[]
        {
            LinuxSite("running-app", state: "Running"),
            LinuxSite("stopped-app", state: "Stopped"),
        };
        var adapter = new FakeWebSitesAdapter(new[] { Page(snapshots, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(includeStopped: true), CancellationToken.None);

        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAsync_PassesCursorAndResourceGroupAndLimit_ToAdapter()
    {
        var adapter = new FakeWebSitesAdapter(new[] { Page(Array.Empty<AzureWebSiteSnapshot>(), nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        await sut.ListAsync(
            new AzureDiscoveryRequest(
                SubscriptionId: SubscriptionId,
                ResourceGroup: "rg-prod",
                IncludeStopped: false,
                Limit: 42,
                Cursor: "opaque-cursor",
                IncludeKubeconfig: false),
            CancellationToken.None);

        adapter.LastCall.Should().NotBeNull();
        adapter.LastCall!.Value.SubscriptionId.Should().Be(SubscriptionId);
        adapter.LastCall.Value.ResourceGroup.Should().Be("rg-prod");
        adapter.LastCall.Value.PageSize.Should().Be(42);
        adapter.LastCall.Value.ContinuationToken.Should().Be("opaque-cursor");
    }

    [Fact]
    public async Task ListAsync_ConsumesOnePageOnly_AndReturnsItsContinuationToken()
    {
        // Adapter yields two pages — backend must consume only the first and surface its NextCursor.
        var p1 = Page(new[] { LinuxSite("a"), LinuxSite("b") }, nextToken: "next-page-token");
        var p2 = Page(new[] { LinuxSite("c") }, nextToken: null);
        var adapter = new FakeWebSitesAdapter(new[] { p1, p2 });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        page.Items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "a", "b" });
        page.NextCursor.Should().Be("next-page-token");
        adapter.PagesEnumerated.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_CursorRoundTrip_NextCallUsesPriorNextCursor()
    {
        // Adapter ignores the cursor (test isolates that the backend forwards it).
        var p1 = Page(new[] { LinuxSite("a") }, nextToken: "page-2");
        var adapter = new FakeWebSitesAdapter(new[] { p1 });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var first = await sut.ListAsync(Request(), CancellationToken.None);
        first.NextCursor.Should().Be("page-2");

        await sut.ListAsync(Request(cursor: first.NextCursor), CancellationToken.None);
        adapter.LastCall!.Value.ContinuationToken.Should().Be("page-2");
    }

    [Fact]
    public async Task ListAsync_NetFrameworkVersionSite_PopulatesRuntimeStack()
    {
        var snap = new AzureWebSiteSnapshot(
            ResourceId: "/x/sites/win-app",
            Name: "win-app",
            Location: "westeurope",
            State: "Running",
            Kind: "app",
            DefaultHostName: null,
            LinuxFxVersion: null,
            NetFrameworkVersion: "v6.0",
            NumberOfWorkers: 1);
        var adapter = new FakeWebSitesAdapter(new[] { Page(new[] { snap }, nextToken: null) });
        var sut = new DefaultAzureWebAppsDiscovery(adapter);

        var page = await sut.ListAsync(Request(), CancellationToken.None);

        var item = page.Items.Should().ContainSingle().Subject;
        item.RuntimeStack.Should().Be("v6.0");
        item.RuntimeVersion.Should().Be("v6.0");
    }

    // --- helpers -----------------------------------------------------------

    private static AzureDiscoveryRequest Request(
        bool includeStopped = false, string? cursor = null) =>
        new(
            SubscriptionId: SubscriptionId,
            ResourceGroup: null,
            IncludeStopped: includeStopped,
            Limit: 100,
            Cursor: cursor,
            IncludeKubeconfig: false);

    private static AzurePage<AzureWebSiteSnapshot> Page(
        IReadOnlyList<AzureWebSiteSnapshot> items, string? nextToken) =>
        new(items, nextToken);

    private static AzureWebSiteSnapshot LinuxSite(string name, string state = "Running") => new(
        ResourceId: $"/x/sites/{name}",
        Name: name,
        Location: "westeurope",
        State: state,
        Kind: "app,linux",
        DefaultHostName: $"{name}.azurewebsites.net",
        LinuxFxVersion: "DOTNETCORE|8.0",
        NetFrameworkVersion: null,
        NumberOfWorkers: 1);

    private static AzureWebSiteSnapshot WindowsSite(string name) => new(
        ResourceId: $"/x/sites/{name}",
        Name: name,
        Location: "westeurope",
        State: "Running",
        Kind: "app",
        DefaultHostName: $"{name}.azurewebsites.net",
        LinuxFxVersion: null,
        NetFrameworkVersion: "v8.0",
        NumberOfWorkers: 1);

    private sealed class FakeWebSitesAdapter : IAzureWebSiteCollectionAdapter
    {
        private readonly IReadOnlyList<AzurePage<AzureWebSiteSnapshot>> _pages;
        public (string SubscriptionId, string? ResourceGroup, int PageSize, string? ContinuationToken)? LastCall { get; private set; }
        public int PagesEnumerated { get; private set; }

        public FakeWebSitesAdapter(IReadOnlyList<AzurePage<AzureWebSiteSnapshot>> pages)
        {
            _pages = pages;
        }

        public async IAsyncEnumerable<AzurePage<AzureWebSiteSnapshot>> GetSitesAsPagesAsync(
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
