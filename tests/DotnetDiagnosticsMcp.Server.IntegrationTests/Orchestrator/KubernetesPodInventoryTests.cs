using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

public class KubernetesPodInventoryTests
{
    private static V1Pod BuildPod(
        string @namespace = "diagnosticsmcp",
        string name = "api-0",
        IDictionary<string, string>? labels = null,
        string phase = "Running",
        bool ready = true,
        string containerName = "app",
        IList<V1VolumeMount>? mounts = null,
        long? runAsUser = 10001,
        IList<V1EnvVar>? env = null,
        IList<V1OwnerReference>? owners = null,
        string image = "myapp:1.0")
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = @namespace,
                Labels = labels,
                CreationTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                OwnerReferences = owners,
            },
            Spec = new V1PodSpec
            {
                NodeName = "node-1",
                SecurityContext = new V1PodSecurityContext { RunAsUser = runAsUser },
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = containerName,
                        Image = image,
                        VolumeMounts = mounts,
                        Env = env,
                    },
                },
            },
            Status = new V1PodStatus
            {
                Phase = phase,
                Conditions = new List<V1PodCondition>
                {
                    new() { Type = "Ready", Status = ready ? "True" : "False" },
                },
            },
        };
    }

    private sealed class StubPodsApi : IKubernetesPodsApi
    {
        private readonly List<V1Pod> _pods;
        public string? CapturedNamespace { get; private set; }
        public string? CapturedLabelSelector { get; private set; }
        public int? CapturedLimit { get; private set; }
        public string? CapturedContinue { get; private set; }
        public string? NextContinueToken { get; init; }

        public StubPodsApi(IEnumerable<V1Pod> pods) => _pods = pods.ToList();

        public Task<V1PodList> ListPodsAsync(
            string? namespaceName,
            string? labelSelector,
            string? fieldSelector,
            int? limit,
            string? continueToken,
            CancellationToken cancellationToken)
        {
            CapturedNamespace = namespaceName;
            CapturedLabelSelector = labelSelector;
            CapturedLimit = limit;
            CapturedContinue = continueToken;
            return Task.FromResult(new V1PodList
            {
                Items = _pods,
                Metadata = new V1ListMeta { ContinueProperty = NextContinueToken },
            });
        }
    }

    private static KubernetesPodInventory NewInventory(
        IEnumerable<V1Pod> pods,
        OrchestratorOptions options,
        out StubPodsApi api)
    {
        api = new StubPodsApi(pods);
        return new KubernetesPodInventory(api, options, NullLogger<KubernetesPodInventory>.Instance);
    }

    [Fact]
    public async Task NamespaceNotAllowed_WhenNamespaceMissingFromAllowlist()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("diagnosticsmcp");
        var sut = NewInventory(Array.Empty<V1Pod>(), options, out _);

        var act = () => sut.ListPodsAsync(new ListPodsRequest(Namespace: "other", null, null, null), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.NamespaceNotAllowed);
    }

    [Fact]
    public async Task NamespaceWildcard_ListsAcrossAllNamespacesWhenNoNamespacePassed()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("*");
        var sut = NewInventory(new[] { BuildPod() }, options, out var api);

        await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: null, LabelSelector: null, FieldSelector: null, ContainerName: null, PreparedOnly: false),
            CancellationToken.None);

        api.CapturedNamespace.Should().BeNull();
    }

    [Fact]
    public async Task DefaultNamespace_UsedWhenCallerOmitsNamespace()
    {
        var options = new OrchestratorOptions
        {
            Enabled = true,
            DefaultNamespace = "diagnosticsmcp",
        };
        options.NamespaceAllowlist.Add("diagnosticsmcp");
        var sut = NewInventory(new[] { BuildPod() }, options, out var api);

        await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: null, LabelSelector: null, FieldSelector: null, ContainerName: null, PreparedOnly: false),
            CancellationToken.None);

        api.CapturedNamespace.Should().Be("diagnosticsmcp");
    }

    [Fact]
    public async Task SelectorRejected_WhenLabelKeyNotInAllowlist()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        options.LabelKeyAllowlist.Add("app");
        var sut = NewInventory(Array.Empty<V1Pod>(), options, out _);

        var act = () => sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: "app=api,env=prod", FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.SelectorRejected);
        ex.Which.Message.Should().Contain("env");
    }

    [Fact]
    public async Task SelectorAccepted_WhenAllLabelKeysInAllowlist()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        options.LabelKeyAllowlist.Add("app");
        options.LabelKeyAllowlist.Add("env");
        var sut = NewInventory(new[] { BuildPod() }, options, out var api);

        await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: "app=api,env=prod", FieldSelector: null, ContainerName: null, PreparedOnly: false),
            CancellationToken.None);

        api.CapturedLabelSelector.Should().Be("app=api,env=prod");
    }

    [Fact]
    public async Task PreparedOnly_ExcludesPodsWithoutPreparedLabel()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var sut = NewInventory(
            new[]
            {
                BuildPod(name: "prepared", labels: preparedLabels),
                BuildPod(name: "unprepared", labels: new Dictionary<string, string> { ["app"] = "api" }),
            },
            options,
            out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        page.Items.Should().HaveCount(1);
        page.Items[0].Name.Should().Be("prepared");
        page.Items[0].DiagnosticsPrepared.Should().BeTrue();
        page.Items[0].PreparationReason.Should().Contain("opt-in label");
    }

    [Fact]
    public async Task PreparedOnlyFalse_AllowsHeuristicMatch_WhenLabelNotRequired()
    {
        var options = new OrchestratorOptions
        {
            Enabled = true,
            RequirePreparedLabel = false,
        };
        options.NamespaceAllowlist.Add("ns");
        var pod = BuildPod(
            mounts: new List<V1VolumeMount> { new() { Name = "diag-tmp", MountPath = "/tmp" } },
            env: new List<V1EnvVar> { new() { Name = "DOTNET_EnableDiagnostics", Value = "1" } });
        var sut = NewInventory(new[] { pod }, options, out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        page.Items.Should().HaveCount(1);
        page.Items[0].DiagnosticsPrepared.Should().BeTrue();
        page.Items[0].PreparationReason.Should().StartWith("heuristic:");
    }

    [Fact]
    public async Task IncludeNotReadyFalse_FiltersOutNotReadyPods()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var sut = NewInventory(
            new[]
            {
                BuildPod(name: "ready", labels: preparedLabels, ready: true),
                BuildPod(name: "notready", labels: preparedLabels, ready: false),
            },
            options,
            out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        page.Items.Select(p => p.Name).Should().Equal("ready");
    }

    [Fact]
    public async Task IncludeNotReadyTrue_KeepsNotReadyPods()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var sut = NewInventory(
            new[]
            {
                BuildPod(name: "ready", labels: preparedLabels, ready: true),
                BuildPod(name: "notready", labels: preparedLabels, ready: false),
            },
            options,
            out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null, IncludeNotReady: true),
            CancellationToken.None);

        page.Items.Select(p => p.Name).Should().BeEquivalentTo(new[] { "ready", "notready" });
    }

    [Fact]
    public async Task ContainerName_PicksMatchingContainer()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var pod = BuildPod(labels: preparedLabels);
        pod.Spec.Containers.Add(new V1Container { Name = "sidecar", Image = "sidecar:1" });
        var sut = NewInventory(new[] { pod }, options, out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: "sidecar"),
            CancellationToken.None);

        page.Items.Should().ContainSingle().Which.ContainerName.Should().Be("sidecar");
    }

    [Fact]
    public async Task ContainerName_NoMatch_SkipsPod()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var sut = NewInventory(new[] { BuildPod(labels: preparedLabels) }, options, out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: "missing"),
            CancellationToken.None);

        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task LimitClamped_ToMaxListLimit()
    {
        var options = new OrchestratorOptions { Enabled = true, MaxListLimit = 10 };
        options.NamespaceAllowlist.Add("ns");
        var sut = NewInventory(Array.Empty<V1Pod>(), options, out var api);

        await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null, Limit: 1000, PreparedOnly: false),
            CancellationToken.None);

        api.CapturedLimit.Should().Be(10);
    }

    [Fact]
    public async Task InvalidLimit_Throws_InvalidArgument()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var sut = NewInventory(Array.Empty<V1Pod>(), options, out _);

        var act = () => sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null, Limit: 0),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.InvalidArgument);
    }

    [Fact]
    public async Task ContinuationCursor_PassedThroughToApi()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var sut = NewInventory(Array.Empty<V1Pod>(), options, out var api);

        await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null, Cursor: "abc123", PreparedOnly: false),
            CancellationToken.None);

        api.CapturedContinue.Should().Be("abc123");
    }

    [Fact]
    public async Task NextCursor_SurfacedFromApi()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var api = new StubPodsApi(Array.Empty<V1Pod>()) { NextContinueToken = "next-token" };
        var sut = new KubernetesPodInventory(api, options, NullLogger<KubernetesPodInventory>.Instance);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        page.NextCursor.Should().Be("next-token");
    }

    [Fact]
    public async Task FilterLabels_RestrictsSurfacedLabelsToAllowlistPlusPreparedKey()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        options.LabelKeyAllowlist.Add("app");
        var labels = new Dictionary<string, string>
        {
            ["app"] = "api",
            ["env"] = "prod",
            ["diagnostics.dotnet.io/prepared"] = "true",
        };
        var sut = NewInventory(new[] { BuildPod(labels: labels) }, options, out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: "app=api", FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        var surfaced = page.Items[0].Labels;
        surfaced.Keys.Should().BeEquivalentTo(new[] { "app", "diagnostics.dotnet.io/prepared" });
    }

    [Fact]
    public async Task CandidateFields_PopulatedFromPodMetadata()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.NamespaceAllowlist.Add("ns");
        var preparedLabels = new Dictionary<string, string> { ["diagnostics.dotnet.io/prepared"] = "true" };
        var owners = new List<V1OwnerReference>
        {
            new() { Kind = "ReplicaSet", Name = "api-rs-abc" },
        };
        var sut = NewInventory(
            new[] { BuildPod(name: "api-0", labels: preparedLabels, owners: owners, image: "myapp:2.3") },
            options,
            out _);

        var page = await sut.ListPodsAsync(
            new ListPodsRequest(Namespace: "ns", LabelSelector: null, FieldSelector: null, ContainerName: null),
            CancellationToken.None);

        var c = page.Items.Single();
        c.Namespace.Should().Be("diagnosticsmcp");
        c.Name.Should().Be("api-0");
        c.ContainerName.Should().Be("app");
        c.Phase.Should().Be("Running");
        c.Ready.Should().BeTrue();
        c.OwnerKind.Should().Be("ReplicaSet");
        c.OwnerName.Should().Be("api-rs-abc");
        c.ImageRef.Should().Be("myapp:2.3");
        c.NodeName.Should().Be("node-1");
        c.ActiveInvestigationCount.Should().Be(0);
    }
}
