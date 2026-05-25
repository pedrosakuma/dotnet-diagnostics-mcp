using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Unit tests for the <see cref="DiscoverAzureTool"/> contract (#232, parent #230).
/// Backend interfaces are mocked so no real Azure SDK call is made — the real backends
/// land in #233 / #234.
/// </summary>
public sealed class DiscoverAzureToolTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task MissingSubscriptionId_ReturnsStructuredError()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: " ",
            kind: DiscoverAzureTool.KindWebApps);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(AzureDiscoveryErrorKinds.InvalidArgument);
        result.Error.Detail.Should().Be("subscriptionId");
        fx.WebApps.CallCount.Should().Be(0);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UnknownKind_ReturnsInvalidArgument()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: "vmss");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(AzureDiscoveryErrorKinds.InvalidArgument);
        result.Error.Message.Should().Contain("kind");
        result.Error.Message.Should().Contain("webapps");
        result.Error.Message.Should().Contain("containerapps");
        result.Error.Message.Should().Contain("aksclusters");
        fx.WebApps.CallCount.Should().Be(0);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DefaultKind_IsWebApps()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId);

        result.IsError.Should().BeFalse();
        result.Data!.Kind.Should().Be(DiscoverAzureTool.KindWebApps);
        result.Data.WebApps.Should().NotBeNull();
        result.Data.ContainerApps.Should().BeNull();
        result.Data.AksClusters.Should().BeNull();
        fx.WebApps.CallCount.Should().Be(1);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task KindWebApps_DispatchesToWebAppsBackendOnly()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: DiscoverAzureTool.KindWebApps);

        result.IsError.Should().BeFalse();
        result.Data!.Kind.Should().Be(DiscoverAzureTool.KindWebApps);
        result.Data.WebApps.Should().NotBeNull();
        result.Data.WebApps!.Items.Should().HaveCount(1);
        result.Data.WebApps.Items[0].Name.Should().Be("foo");
        result.Data.ContainerApps.Should().BeNull();
        result.Data.AksClusters.Should().BeNull();
        fx.WebApps.CallCount.Should().Be(1);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task KindContainerApps_DispatchesToContainerAppsBackendOnly()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: DiscoverAzureTool.KindContainerApps);

        result.IsError.Should().BeFalse();
        result.Data!.Kind.Should().Be(DiscoverAzureTool.KindContainerApps);
        result.Data.ContainerApps.Should().NotBeNull();
        result.Data.ContainerApps!.Items.Should().HaveCount(1);
        result.Data.ContainerApps.Items[0].Name.Should().Be("bar");
        result.Data.WebApps.Should().BeNull();
        result.Data.AksClusters.Should().BeNull();
        fx.WebApps.CallCount.Should().Be(0);
        fx.ContainerApps.CallCount.Should().Be(1);
        fx.Aks.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task KindAksClusters_DispatchesToAksBackendOnly()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: DiscoverAzureTool.KindAksClusters);

        result.IsError.Should().BeFalse();
        result.Data!.Kind.Should().Be(DiscoverAzureTool.KindAksClusters);
        result.Data.AksClusters.Should().NotBeNull();
        result.Data.AksClusters!.Items.Should().HaveCount(1);
        result.Data.AksClusters.Items[0].Name.Should().Be("baz");
        result.Data.WebApps.Should().BeNull();
        result.Data.ContainerApps.Should().BeNull();
        fx.WebApps.CallCount.Should().Be(0);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchedRequest_PreservesArgumentsAndClampsLimit()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: "  " + SubscriptionId + "  ",
            kind: DiscoverAzureTool.KindAksClusters,
            resourceGroup: "  rg-prod  ",
            includeStopped: true,
            limit: 10_000,
            cursor: "page-2",
            includeKubeconfig: true);

        result.IsError.Should().BeFalse();
        fx.Aks.LastRequest.Should().NotBeNull();
        var req = fx.Aks.LastRequest!;
        req.SubscriptionId.Should().Be(SubscriptionId);
        req.ResourceGroup.Should().Be("rg-prod");
        req.IncludeStopped.Should().BeTrue();
        req.Limit.Should().Be(DiscoverAzureTool.MaxLimit);
        req.Cursor.Should().Be("page-2");
        req.IncludeKubeconfig.Should().BeTrue();
    }

    [Fact]
    public async Task NonPositiveLimit_FallsBackToDefault()
    {
        var fx = new Fixture();
        var tool = new DiscoverAzureTool();

        await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: DiscoverAzureTool.KindWebApps,
            limit: 0);

        fx.WebApps.LastRequest.Should().NotBeNull();
        fx.WebApps.LastRequest!.Limit.Should().Be(100);
    }

    [Fact]
    public async Task AzureDiscoveryDisabled_ReturnsStructuredError()
    {
        var fx = new Fixture();
        fx.Options.Enabled = false;
        var tool = new DiscoverAzureTool();

        var result = await tool.DiscoverAzureAsync(
            fx.WebApps, fx.ContainerApps, fx.Aks, fx.Options,
            subscriptionId: SubscriptionId,
            kind: DiscoverAzureTool.KindWebApps);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(AzureDiscoveryErrorKinds.AzureDiscoveryDisabled);
        fx.WebApps.CallCount.Should().Be(0);
        fx.ContainerApps.CallCount.Should().Be(0);
        fx.Aks.CallCount.Should().Be(0);
    }

    private sealed class Fixture
    {
        public RecordingWebApps WebApps { get; } = new();
        public RecordingContainerApps ContainerApps { get; } = new();
        public RecordingAks Aks { get; } = new();
        public AzureDiscoveryOptions Options { get; } = new() { Enabled = true };
    }

    private sealed class RecordingWebApps : IAzureWebAppsDiscovery
    {
        public int CallCount { get; private set; }
        public AzureDiscoveryRequest? LastRequest { get; private set; }
        public Task<AzurePagedResult<AzureWebAppCandidate>> ListAsync(
            AzureDiscoveryRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var item = new AzureWebAppCandidate(
                ResourceId: "/subs/x/sites/foo",
                Name: "foo",
                Location: "westeurope",
                DefaultHostName: "foo.azurewebsites.net",
                RuntimeStack: "DOTNETCORE|8.0",
                RuntimeVersion: "8.0",
                InstanceCount: 1,
                State: "Running",
                Kind: "app,linux",
                ReadinessWarnings: Array.Empty<string>());
            return Task.FromResult(new AzurePagedResult<AzureWebAppCandidate>(new[] { item }, null));
        }
    }

    private sealed class RecordingContainerApps : IAzureContainerAppsDiscovery
    {
        public int CallCount { get; private set; }
        public AzureDiscoveryRequest? LastRequest { get; private set; }
        public Task<AzurePagedResult<AzureContainerAppCandidate>> ListAsync(
            AzureDiscoveryRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var item = new AzureContainerAppCandidate(
                ResourceId: "/subs/x/containerApps/bar",
                Name: "bar",
                Location: "westeurope",
                LatestRevisionFqdn: "bar.example.io",
                ContainerImages: new[] { "registry.io/img:1" },
                MinReplicas: 1,
                MaxReplicas: 3,
                ProvisioningState: "Succeeded",
                RunningState: "Running",
                ReadinessWarnings: Array.Empty<string>());
            return Task.FromResult(new AzurePagedResult<AzureContainerAppCandidate>(new[] { item }, null));
        }
    }

    private sealed class RecordingAks : IAzureAksDiscovery
    {
        public int CallCount { get; private set; }
        public AzureDiscoveryRequest? LastRequest { get; private set; }
        public Task<AzurePagedResult<AzureAksClusterCandidate>> ListAsync(
            AzureDiscoveryRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            var item = new AzureAksClusterCandidate(
                ResourceId: "/subs/x/managedClusters/baz",
                Name: "baz",
                Location: "westeurope",
                Fqdn: "baz.hcp.westeurope.azmk8s.io",
                KubernetesVersion: "1.30.0",
                AgentPoolCount: 2,
                NodeResourceGroup: "MC_x_baz_westeurope",
                Handoff: null,
                ReadinessWarnings: Array.Empty<string>());
            return Task.FromResult(new AzurePagedResult<AzureAksClusterCandidate>(new[] { item }, null));
        }
    }
}
