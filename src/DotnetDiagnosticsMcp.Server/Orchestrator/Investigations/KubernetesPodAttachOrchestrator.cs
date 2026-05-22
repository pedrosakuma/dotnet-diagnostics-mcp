using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Default <see cref="IPodAttachOrchestrator"/> implementation backed by the
/// orchestrator's <see cref="IKubernetesPodsApi"/> + <see cref="OrchestratorOptions"/>.
/// </summary>
/// <remarks>
/// <para>Flow per docs/central-orchestrator-design.md §5.4:</para>
/// <list type="number">
/// <item>Validate namespace via the existing allowlist policy.</item>
/// <item>Reuse an in-flight or active handle for the same target when allowed.</item>
/// <item>Read the Pod to confirm phase=Running, the target container exists, and (when required) it carries the prepared label.</item>
/// <item>Mint a fresh per-attach bearer token, build a <see cref="V1EphemeralContainer"/> pinned to the target container's PID namespace, and patch the Pod.</item>
/// <item>Register the handle in Attaching state, poll <c>ephemeralContainerStatuses</c> until Running or timeout, transition to Active or Failed accordingly.</item>
/// </list>
/// <para>The proxy that makes a returned handle usable as a transport lands in P3b-2.</para>
/// </remarks>
internal sealed class KubernetesPodAttachOrchestrator : IPodAttachOrchestrator
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly IKubernetesPodsApi _podsApi;
    private readonly IInvestigationStore _store;
    private readonly OrchestratorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KubernetesPodAttachOrchestrator> _logger;
    private readonly TimeSpan _pollInterval;

    public KubernetesPodAttachOrchestrator(
        IKubernetesPodsApi podsApi,
        IInvestigationStore store,
        OrchestratorOptions options,
        ILogger<KubernetesPodAttachOrchestrator> logger)
        : this(podsApi, store, options, TimeProvider.System, DefaultPollInterval, logger)
    {
    }

    internal KubernetesPodAttachOrchestrator(
        IKubernetesPodsApi podsApi,
        IInvestigationStore store,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        TimeSpan pollInterval,
        ILogger<KubernetesPodAttachOrchestrator> logger)
    {
        _podsApi = podsApi;
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _pollInterval = pollInterval;
        _logger = logger;
    }

    public async Task<InvestigationHandle> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ns = ResolveAndValidateNamespace(request.Namespace);
        if (string.IsNullOrWhiteSpace(request.PodName))
        {
            throw new OrchestratorException(OrchestratorErrorKinds.InvalidArgument, "podName is required.");
        }

        var pod = await ReadPodOrThrowAsync(ns, request.PodName, cancellationToken).ConfigureAwait(false);
        var container = SelectContainerOrThrow(pod, request.ContainerName);
        ValidatePodRunning(pod);
        ValidatePodPrepared(pod, request.RequirePreparedTarget);

        var now = _timeProvider.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(request.TtlSeconds ?? _options.DefaultInvestigationTtlSeconds);
        var token = GenerateBearerToken();
        var ephemeralName = BuildEphemeralContainerName();
        var handleId = "inv_" + RandomHex(16);

        var handle = new InvestigationHandle(
            HandleId: handleId,
            Namespace: ns,
            PodName: request.PodName,
            TargetContainerName: container.Name,
            EphemeralContainerName: ephemeralName,
            PodLocalBearerToken: token,
            State: InvestigationState.Attaching,
            AttachedAt: now,
            ExpiresAt: now + ttl);

        // Atomic check-and-reserve: when reuse is allowed and a target tuple already has an
        // Active/Attaching handle, return it instead of patching a second ephemeral container.
        // The single lock-protected operation prevents two concurrent attaches for the same
        // target from both creating an ephemeral container.
        if (!_store.TryReserveTarget(handle, request.AllowReuseExistingSession, out var existing))
        {
            _logger.LogInformation(
                "Reusing investigation handle {HandleId} for {Namespace}/{Pod}/{Container} (state={State}).",
                existing!.HandleId, ns, request.PodName, container.Name, existing.State);
            return existing;
        }

        try
        {
            var spec = BuildEphemeralContainerSpec(ephemeralName, container.Name, token);
            await PatchEphemeralContainerAsync(ns, request.PodName, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkFailed(handle, "Attach canceled by caller before the ephemeral container patch completed.");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(handle, ex.Message);
            throw;
        }

        try
        {
            await WaitForEphemeralRunningAsync(ns, request.PodName, ephemeralName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkFailed(handle, "Attach canceled by caller while waiting for ephemeral container readiness.");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(handle, ex.Message);
            throw;
        }

        var active = handle with { State = InvestigationState.Active };
        _store.Update(active);
        _logger.LogInformation(
            "Attached investigation {HandleId} to {Namespace}/{Pod}/{Container} as ephemeral '{EphemeralName}'.",
            active.HandleId, ns, request.PodName, container.Name, ephemeralName);
        return active;
    }

    private void MarkFailed(InvestigationHandle handle, string reason)
    {
        var failed = handle with { State = InvestigationState.Failed, FailureReason = reason };
        _store.Update(failed);
    }

    private string ResolveAndValidateNamespace(string? requested)
    {
        var ns = string.IsNullOrWhiteSpace(requested) ? _options.DefaultNamespace : requested;
        var allowlist = _options.NamespaceAllowlist;
        var wildcard = allowlist.Count == 1 && allowlist[0] == "*";

        if (string.IsNullOrWhiteSpace(ns))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.NamespaceNotAllowed,
                "No namespace supplied and no DefaultNamespace configured.");
        }
        if (!wildcard && !allowlist.Contains(ns, StringComparer.Ordinal))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.NamespaceNotAllowed,
                $"Namespace '{ns}' is not in the orchestrator's NamespaceAllowlist. " +
                $"Allowed: [{string.Join(", ", allowlist)}].");
        }
        return ns!;
    }

    private async Task<V1Pod> ReadPodOrThrowAsync(string ns, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _podsApi.ReadPodAsync(ns, name, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PodNotFound,
                $"Pod '{ns}/{name}' was not found.", ex);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"Kubernetes API rejected the read pod call with {(int?)ex.Response?.StatusCode}. " +
                "Check the orchestrator ServiceAccount has 'pods' get in the requested namespace.", ex);
        }
        catch (HttpOperationException ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                $"Kubernetes API call failed: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
        }
    }

    private static V1Container SelectContainerOrThrow(V1Pod pod, string? requested)
    {
        var containers = pod.Spec?.Containers;
        if (containers is null || containers.Count == 0)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ContainerNotFound,
                $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' has no containers.");
        }
        if (string.IsNullOrEmpty(requested)) return containers[0];

        var match = containers.FirstOrDefault(c => string.Equals(c.Name, requested, StringComparison.Ordinal));
        if (match is null)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ContainerNotFound,
                $"Container '{requested}' not found on pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}'. " +
                $"Available: [{string.Join(", ", containers.Select(c => c.Name))}].");
        }
        return match;
    }

    private static void ValidatePodRunning(V1Pod pod)
    {
        var phase = pod.Status?.Phase;
        if (!string.Equals(phase, "Running", StringComparison.Ordinal))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PodNotRunning,
                $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' is in phase '{phase ?? "Unknown"}'. " +
                "Only Running pods can be attached.");
        }
    }

    private void ValidatePodPrepared(V1Pod pod, bool callerRequiresPrepared)
    {
        if (!callerRequiresPrepared && !_options.RequirePreparedLabel) return;

        var labels = pod.Metadata?.Labels;
        var hasLabel = labels is not null &&
            labels.TryGetValue(_options.PreparedLabelKey, out var v) &&
            string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        if (hasLabel) return;

        throw new OrchestratorException(
            OrchestratorErrorKinds.PodNotPrepared,
            $"Pod '{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name}' is missing opt-in label " +
            $"'{_options.PreparedLabelKey}=true'. Add the label (and a shared /tmp emptyDir + matching UID) or " +
            "set requirePreparedTarget=false (and Orchestrator:RequirePreparedLabel=false) to override.");
    }

    private string BuildEphemeralContainerName()
    {
        // Pod ephemeralContainers[*].name must be unique within the pod. Suffix a short
        // random tag so reattaching after a previous (non-removable) ephemeral container
        // doesn't collide.
        return _options.EphemeralContainerNamePrefix + RandomHex(4);
    }

    private V1EphemeralContainer BuildEphemeralContainerSpec(string ephemeralName, string targetContainer, string token)
    {
        return new V1EphemeralContainer
        {
            Name = ephemeralName,
            Image = _options.EphemeralContainerImage,
            ImagePullPolicy = "IfNotPresent",
            // Required: join the target container's PID namespace so the diagnostic IPC
            // socket at /tmp/dotnet-diagnostic-<pid> is visible.
            TargetContainerName = targetContainer,
            Env = new List<V1EnvVar>
            {
                new() { Name = "MCP_BEARER_TOKEN", Value = token },
                new() { Name = "ASPNETCORE_URLS", Value = $"http://0.0.0.0:{_options.ProxyPodPort}" },
            },
            TerminationMessagePolicy = "File",
        };
    }

    private async Task PatchEphemeralContainerAsync(string ns, string name, V1EphemeralContainer ephemeral, CancellationToken cancellationToken)
    {
        try
        {
            await _podsApi.AddEphemeralContainerAsync(ns, name, ephemeral, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"Kubernetes API rejected the ephemeralcontainers patch with {(int?)ex.Response?.StatusCode}. " +
                "Check the orchestrator ServiceAccount has 'pods/ephemeralcontainers' patch in the namespace.", ex);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.AttachAlreadyInProgress,
                "Kubernetes reported a conflict applying the ephemeralcontainers patch. " +
                "Another attach may be in flight for this pod.", ex);
        }
        catch (HttpOperationException ex)
        {
            // The patch was not accepted by the API server, so AttachFailed (which the design
            // reserves for an accepted-but-unhealthy ephemeral container) is the wrong kind.
            // Surface transient API failures as KubeApiUnavailable so the caller knows a retry
            // is appropriate without operator intervention.
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                $"Failed to patch ephemeralcontainers: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
        }
    }

    private async Task WaitForEphemeralRunningAsync(
        string ns,
        string name,
        string ephemeralName,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(_options.AttachReadinessTimeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            V1Pod pod;
            try
            {
                pod = await _podsApi.ReadPodAsync(ns, name, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException ex)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.AttachFailed,
                    $"Failed to poll ephemeral container readiness: {(int?)ex.Response?.StatusCode} {ex.Message}", ex);
            }

            var status = pod.Status?.EphemeralContainerStatuses?
                .FirstOrDefault(s => string.Equals(s.Name, ephemeralName, StringComparison.Ordinal));
            if (status is not null)
            {
                if (status.State?.Running is not null) return;
                if (status.State?.Terminated is not null)
                {
                    throw new OrchestratorException(
                        OrchestratorErrorKinds.AttachFailed,
                        $"Ephemeral container '{ephemeralName}' terminated before becoming ready " +
                        $"(reason={status.State.Terminated.Reason ?? "?"}, exitCode={status.State.Terminated.ExitCode}).");
                }
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.AttachTimeout,
                    $"Ephemeral container '{ephemeralName}' did not become Running within " +
                    $"{_options.AttachReadinessTimeoutSeconds}s on pod '{ns}/{name}'.");
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GenerateBearerToken() => RandomHex(32);

    private static string RandomHex(int byteCount)
    {
        Span<byte> buf = stackalloc byte[64];
        if (byteCount > buf.Length) buf = new byte[byteCount];
        else buf = buf[..byteCount];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
