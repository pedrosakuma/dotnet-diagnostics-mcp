using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

public class KubernetesPodAttachOrchestratorTests
{
    private const string Ns = "diagnosticsmcp";
    private const string Pod = "api-0";
    private const string Container = "app";

    [Fact]
    public async Task AttachAsync_ReturnsActiveHandle_OnHappyPath()
    {
        var api = new StubAttachApi(
            pod: BuildPreparedPod(),
            ephemeralRunningAfter: 1);
        var (orch, store, options) = NewOrchestrator(api);

        var handle = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        handle.State.Should().Be(InvestigationState.Active);
        handle.Namespace.Should().Be(Ns);
        handle.PodName.Should().Be(Pod);
        handle.TargetContainerName.Should().Be(Container);
        handle.HandleId.Should().StartWith("inv_");
        handle.EphemeralContainerName.Should().StartWith(options.EphemeralContainerNamePrefix);
        handle.PodLocalBearerToken.Should().NotBeNullOrWhiteSpace();
        api.PatchInvoked.Should().BeTrue();
        api.PatchedSpec!.Image.Should().Be(options.EphemeralContainerImage);
        api.PatchedSpec.TargetContainerName.Should().Be(Container);
        api.PatchedSpec.Env.Should().Contain(e => e.Name == "MCP_BEARER_TOKEN" && e.Value == handle.PodLocalBearerToken);
        api.PatchedSpec.Env.Should().Contain(e => e.Name == "ASPNETCORE_URLS" && e.Value == $"http://0.0.0.0:{options.ProxyPodPort}");
        api.PatchedSpec.Args.Should().Equal("--urls", $"http://0.0.0.0:{options.ProxyPodPort}");
        store.GetById(handle.HandleId).Should().BeSameAs(handle);
    }

    [Fact]
    public async Task AttachAsync_HonoursConfiguredProxyPodPort_InEphemeralContainerEnv()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, options) = NewOrchestrator(api);
        options.ProxyPodPort = 18888;

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.Env.Should().Contain(e => e.Name == "ASPNETCORE_URLS" && e.Value == "http://0.0.0.0:18888");
        api.PatchedSpec.Args.Should().Equal("--urls", "http://0.0.0.0:18888");
    }

    [Fact]
    public async Task AttachAsync_InheritsTargetVolumeMounts_SoSharedTmpSocketIsVisible()
    {
        // Regression guard for the central topology: without this the ephemeral
        // container would have its own /tmp and the diagnostic IPC socket created
        // by the target's runtime at /tmp/dotnet-diagnostic-<pid> would be
        // invisible to it, breaking list_dotnet_processes through the proxy.
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].VolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = "diag-tmp", MountPath = "/tmp" },
            new() { Name = "ro-config", MountPath = "/config", ReadOnlyProperty = true },
        };
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.VolumeMounts.Should().NotBeNull();
        api.PatchedSpec.VolumeMounts.Should().HaveCount(2);
        api.PatchedSpec.VolumeMounts!.Should().ContainSingle(v =>
            v.Name == "diag-tmp" && v.MountPath == "/tmp");
        api.PatchedSpec.VolumeMounts!.Should().ContainSingle(v =>
            v.Name == "ro-config" && v.MountPath == "/config" && v.ReadOnlyProperty == true);
    }

    [Fact]
    public async Task AttachAsync_TargetWithoutVolumeMounts_LeavesEphemeralVolumeMountsNull()
    {
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].VolumeMounts = null;
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.VolumeMounts.Should().BeNull(
            "container-level security context is optional and so is volumeMounts");
    }

    [Fact]
    public async Task AttachAsync_InheritsTargetSecurityContext_RunAsUserAndGroup()
    {
        // The ephemeral container must run as the same UID as the target so the
        // diagnostic IPC socket file (mode 0600 owned by the runtime's effective
        // uid) is readable. It also inherits non-elevating restrictions
        // (allowPrivilegeEscalation=false, capability drops, seccomp profile,
        // MAC contexts) so it survives Pod Security "restricted" admission.
        // Privileged=true and capability adds are intentionally dropped.
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].SecurityContext = new V1SecurityContext
        {
            RunAsUser = 10001,
            RunAsGroup = 10001,
            RunAsNonRoot = true,
            Privileged = true, // must be dropped
            AllowPrivilegeEscalation = false,
            Capabilities = new V1Capabilities
            {
                Add = new List<string> { "NET_ADMIN" }, // must be dropped
                Drop = new List<string> { "ALL" },
            },
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
        };
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ctx = api.PatchedSpec!.SecurityContext;
        ctx.Should().NotBeNull();
        ctx!.RunAsUser.Should().Be(10001);
        ctx.RunAsGroup.Should().Be(10001);
        ctx.RunAsNonRoot.Should().Be(true);
        ctx.Privileged.Should().BeNull(
            "the orchestrator must not silently propagate elevated privileges");
        ctx.AllowPrivilegeEscalation.Should().Be(false,
            "non-elevating restrictions must be inherited for PSS-restricted admission");
        ctx.Capabilities.Should().NotBeNull();
        ctx.Capabilities!.Add.Should().BeNullOrEmpty(
            "capability adds are workload-specific elevations and must not propagate");
        ctx.Capabilities.Drop.Should().BeEquivalentTo(new[] { "ALL" });
        ctx.SeccompProfile.Should().NotBeNull();
        ctx.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotFound_WhenApiReturns404()
    {
        var api = new StubAttachApi(readPodException: NewHttpEx(HttpStatusCode.NotFound));
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotFound);
    }

    [Fact]
    public async Task AttachAsync_ThrowsContainerNotFound_WhenContainerMissing()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod());
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(containerName: "does-not-exist"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.ContainerNotFound);
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotRunning_WhenPhasePending()
    {
        var pod = BuildPreparedPod();
        pod.Status.Phase = "Pending";
        var api = new StubAttachApi(pod: pod);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotRunning);
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotPrepared_WhenLabelMissing()
    {
        var pod = BuildPreparedPod();
        pod.Metadata.Labels = new Dictionary<string, string>(); // drop opt-in label
        var api = new StubAttachApi(pod: pod);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotPrepared);
    }

    [Fact]
    public async Task AttachAsync_AllowsUnpreparedPod_WhenCallerOptsOut()
    {
        var pod = BuildPreparedPod();
        pod.Metadata.Labels = new Dictionary<string, string>();
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api, requirePreparedLabel: false);

        var handle = await orch.AttachAsync(NewRequest(requirePreparedTarget: false), CancellationToken.None);

        handle.State.Should().Be(InvestigationState.Active);
    }

    [Fact]
    public async Task AttachAsync_ReusesExistingActiveHandle()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        api.PatchInvocationCount.Should().Be(1);

        var second = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        second.Should().BeSameAs(first);
        api.PatchInvocationCount.Should().Be(1);
        store.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task AttachAsync_PatchesAgain_WhenReuseDisabled()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        var second = await orch.AttachAsync(NewRequest(allowReuseExistingSession: false), CancellationToken.None);

        second.HandleId.Should().NotBe(first.HandleId);
        api.PatchInvocationCount.Should().Be(2);
        store.Snapshot().Should().HaveCount(2);
    }

    [Fact]
    public async Task AttachAsync_ThrowsAttachTimeout_WhenEphemeralNeverRuns()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod()); // never reports running
        var (orch, store, _) = NewOrchestrator(api, attachTimeoutSeconds: 1);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.AttachTimeout);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_MapsForbiddenPatch_ToPermissionDenied()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), patchException: NewHttpEx(HttpStatusCode.Forbidden));
        var (orch, store, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_ThrowsNamespaceNotAllowed_WhenNamespaceMissingFromAllowlist()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod());
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(@namespace: "kube-system"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.NamespaceNotAllowed);
    }

    [Fact]
    public async Task AttachAsync_MapsServerErrorPatch_ToKubeApiUnavailable()
    {
        // 500/503 during the ephemeralcontainers patch must NOT be reported as AttachFailed
        // (which the design reserves for an accepted-but-unhealthy ephemeral container).
        var api = new StubAttachApi(pod: BuildPreparedPod(), patchException: NewHttpEx(HttpStatusCode.InternalServerError));
        var (orch, store, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.KubeApiUnavailable);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_OnCancellation_TransitionsHandleToFailed()
    {
        // Regression: cancellation must not leave the registered handle stuck in Attaching,
        // otherwise FindReusableTarget would return a permanently-orphaned handle on retry.
        var api = new StubAttachApi(pod: BuildPreparedPod()); // never reports running
        var (orch, store, _) = NewOrchestrator(api, attachTimeoutSeconds: 60);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => orch.AttachAsync(NewRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Snapshot().Should().ContainSingle(h =>
            h.State == InvestigationState.Failed &&
            h.FailureReason != null &&
            h.FailureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvestigationHandle_SerializedShape_ExcludesBearerToken()
    {
        // Defence in depth: even if a future caller serializes the internal handle directly,
        // [JsonIgnore] on PodLocalBearerToken must keep the secret out of the wire shape.
        var handle = new InvestigationHandle(
            HandleId: "inv_test",
            Namespace: Ns,
            PodName: Pod,
            TargetContainerName: Container,
            EphemeralContainerName: "dotnet-dbg-mcp-abcd",
            PodLocalBearerToken: "SECRET_TOKEN_VALUE",
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

        var json = System.Text.Json.JsonSerializer.Serialize(handle);

        json.Should().NotContain("SECRET_TOKEN_VALUE");
        json.Should().NotContain("PodLocalBearerToken");
    }

    [Fact]
    public void AttachSession_FromHandle_DropsBearerToken()
    {
        var handle = new InvestigationHandle(
            HandleId: "inv_test",
            Namespace: Ns,
            PodName: Pod,
            TargetContainerName: Container,
            EphemeralContainerName: "dotnet-dbg-mcp-abcd",
            PodLocalBearerToken: "SECRET_TOKEN_VALUE",
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

        var session = AttachSession.FromHandle(handle);
        var json = System.Text.Json.JsonSerializer.Serialize(session);

        session.HandleId.Should().Be(handle.HandleId);
        json.Should().NotContain("SECRET_TOKEN_VALUE");
    }

    // ---- helpers ----

    private static AttachRequest NewRequest(
        string @namespace = Ns,
        string? containerName = null,
        bool requirePreparedTarget = true,
        bool allowReuseExistingSession = true)
        => new(@namespace, Pod, containerName, TtlSeconds: null, requirePreparedTarget, allowReuseExistingSession);

    private static V1Pod BuildPreparedPod()
        => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = Pod,
                NamespaceProperty = Ns,
                Labels = new Dictionary<string, string>
                {
                    [OrchestratorOptions.DefaultPreparedLabelKey] = "true",
                },
            },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container>
                {
                    new() { Name = Container, Image = "myapp:1.0" },
                },
            },
            Status = new V1PodStatus { Phase = "Running" },
        };

    private static (KubernetesPodAttachOrchestrator orch, IInvestigationStore store, OrchestratorOptions options)
        NewOrchestrator(StubAttachApi api, bool requirePreparedLabel = true, int attachTimeoutSeconds = 10)
    {
        var options = new OrchestratorOptions
        {
            Enabled = true,
            RequirePreparedLabel = requirePreparedLabel,
            AttachReadinessTimeoutSeconds = attachTimeoutSeconds,
        };
        options.NamespaceAllowlist.Add(Ns);
        var store = new MemoryInvestigationStore();
        var services = new ServiceCollection();
        services.AddMetrics();
        var provider = services.BuildServiceProvider();
        var observability = new OrchestratorObservability(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            store,
            new AuditLogWriter(TextWriter.Null));
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(), new MemoryInvestigationSessionBinder());
        var time = new FakeTimeProvider();
        var orch = new KubernetesPodAttachOrchestrator(
            api, store, closer, observability, options, time, TimeSpan.FromMilliseconds(1),
            NullLogger<KubernetesPodAttachOrchestrator>.Instance);
        return (orch, store, options);
    }

    private static HttpOperationException NewHttpEx(HttpStatusCode code)
        => new($"HTTP {(int)code}")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), string.Empty),
        };

    private sealed class StubAttachApi : IKubernetesPodsApi
    {
        private readonly V1Pod? _pod;
        private readonly int _ephemeralRunningAfter;
        private readonly Exception? _readEx;
        private readonly Exception? _patchEx;
        private int _readCount;

        public StubAttachApi(
            V1Pod? pod = null,
            int ephemeralRunningAfter = int.MaxValue,
            Exception? readPodException = null,
            Exception? patchException = null)
        {
            _pod = pod;
            _ephemeralRunningAfter = ephemeralRunningAfter;
            _readEx = readPodException;
            _patchEx = patchException;
        }

        public bool PatchInvoked { get; private set; }
        public int PatchInvocationCount { get; private set; }
        public V1EphemeralContainer? PatchedSpec { get; private set; }

        public Task<V1PodList> ListPodsAsync(string? namespaceName, string? labelSelector, string? fieldSelector, int? limit, string? continueToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<V1Pod> ReadPodAsync(string namespaceName, string name, CancellationToken cancellationToken)
        {
            _readCount++;
            if (_readEx is not null) throw _readEx;
            if (_pod is null) throw new InvalidOperationException("StubAttachApi configured without a pod.");
            if (PatchedSpec is not null)
            {
                // After patch, the read includes the ephemeral container status; flip to Running on the configured tick.
                var statuses = _pod.Status.EphemeralContainerStatuses ??= new List<V1ContainerStatus>();
                var existing = statuses.FirstOrDefault(s => s.Name == PatchedSpec.Name);
                var state = _readCount >= _ephemeralRunningAfter
                    ? new V1ContainerState { Running = new V1ContainerStateRunning() }
                    : new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ContainerCreating" } };
                if (existing is null)
                {
                    statuses.Add(new V1ContainerStatus
                    {
                        Name = PatchedSpec.Name,
                        Image = PatchedSpec.Image,
                        ImageID = string.Empty,
                        Ready = false,
                        RestartCount = 0,
                        State = state,
                    });
                }
                else
                {
                    existing.State = state;
                }
            }
            return Task.FromResult(_pod);
        }

        public Task<V1Pod> AddEphemeralContainerAsync(string namespaceName, string name, V1EphemeralContainer ephemeralContainer, CancellationToken cancellationToken)
        {
            if (_patchEx is not null) throw _patchEx;
            PatchInvoked = true;
            PatchInvocationCount++;
            PatchedSpec = ephemeralContainer;
            _readCount = 0; // restart readiness clock so ephemeralRunningAfter applies post-patch
            return Task.FromResult(_pod!);
        }

        public Task<k8s.IStreamDemuxer> OpenPortForwardAsync(string namespaceName, string name, int podPort, CancellationToken cancellationToken)
            => throw new NotSupportedException("StubAttachApi does not exercise port-forward; use the dedicated KubernetesPortForwardManager tests.");
    }

    private sealed class NoOpProxyClient : IInvestigationProxyClient
    {
        public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(InvestigationHandle handle, ModelContextProtocol.Protocol.CallToolRequestParams request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class NoOpPortForwardManager : IPortForwardManager
    {
        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal manual <see cref="TimeProvider"/> double — advances on every <see cref="GetUtcNow"/>
    /// call so AttachReadinessTimeoutSeconds is reached deterministically in tests without real sleeps.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            var snapshot = _now;
            _now = _now.AddMilliseconds(250);
            return snapshot;
        }
    }
}
