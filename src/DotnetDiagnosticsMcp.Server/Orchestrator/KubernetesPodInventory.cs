using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Default <see cref="IPodInventory"/> implementation. Enforces orchestrator policy
/// (namespace allowlist, label-key allowlist, preparedness heuristic) and adapts
/// <see cref="V1Pod"/> rows into <see cref="PodCandidate"/> envelopes.
/// </summary>
internal sealed class KubernetesPodInventory : IPodInventory
{
    private const string PreparedHeuristicTmpDir = "/tmp";
    private const string DotnetEnableDiagnosticsEnvVar = "DOTNET_EnableDiagnostics";

    private readonly IKubernetesPodsApi _podsApi;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<KubernetesPodInventory> _logger;

    public KubernetesPodInventory(
        IKubernetesPodsApi podsApi,
        OrchestratorOptions options,
        ILogger<KubernetesPodInventory> logger)
    {
        _podsApi = podsApi;
        _options = options;
        _logger = logger;
    }

    public async Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ns = ResolveAndValidateNamespace(request.Namespace);
        ValidateLabelSelector(request.LabelSelector);
        var limit = ClampLimit(request.Limit);

        V1PodList list;
        try
        {
            list = await _podsApi.ListPodsAsync(
                ns,
                request.LabelSelector,
                request.FieldSelector,
                limit,
                request.Cursor,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden ||
                                                ex.Response?.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PermissionDenied,
                $"Kubernetes API rejected the list_pods call with {(int?)ex.Response?.StatusCode}. " +
                "Check the orchestrator ServiceAccount has 'pods' get/list/watch in the requested namespace.",
                ex);
        }
        catch (HttpOperationException ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                $"Kubernetes API call failed: {(int?)ex.Response?.StatusCode} {ex.Message}",
                ex);
        }
        catch (KubernetesConfigurationException ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeApiUnavailable,
                ex.Message, ex);
        }

        var items = new List<PodCandidate>(list.Items?.Count ?? 0);
        foreach (var pod in list.Items ?? Array.Empty<V1Pod>())
        {
            var container = SelectContainer(pod, request.ContainerName);
            if (container is null) continue;

            var (prepared, reason) = EvaluatePreparedness(pod, container);
            if (request.PreparedOnly && !prepared) continue;

            var ready = IsReady(pod);
            if (!request.IncludeNotReady && !ready) continue;

            items.Add(BuildCandidate(pod, container, ready, prepared, reason));
        }

        return new PodCandidatePage(items, list.Metadata?.ContinueProperty);
    }

    private string? ResolveAndValidateNamespace(string? requestedNamespace)
    {
        var ns = string.IsNullOrWhiteSpace(requestedNamespace)
            ? _options.DefaultNamespace
            : requestedNamespace;

        var allowlist = _options.NamespaceAllowlist;
        var wildcard = allowlist.Count == 1 && allowlist[0] == "*";

        if (string.IsNullOrWhiteSpace(ns))
        {
            if (wildcard) return null;
            throw new OrchestratorException(
                OrchestratorErrorKinds.NamespaceNotAllowed,
                "No namespace supplied and no DefaultNamespace configured. " +
                "Pass a namespace explicitly or configure Orchestrator:DefaultNamespace.");
        }

        if (wildcard) return ns;
        if (!allowlist.Contains(ns, StringComparer.Ordinal))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.NamespaceNotAllowed,
                $"Namespace '{ns}' is not in the orchestrator's NamespaceAllowlist. " +
                $"Allowed: [{string.Join(", ", allowlist)}].");
        }

        return ns;
    }

    private void ValidateLabelSelector(string? labelSelector)
    {
        if (string.IsNullOrEmpty(labelSelector)) return;
        if (_options.LabelKeyAllowlist.Count == 0) return;

        foreach (var clause in labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = ExtractSelectorKey(clause);
            if (string.IsNullOrEmpty(key))
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.SelectorRejected,
                    $"Could not parse label selector clause '{clause}'.");
            }
            if (!_options.LabelKeyAllowlist.Contains(key, StringComparer.Ordinal))
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.SelectorRejected,
                    $"Label key '{key}' is not in the orchestrator's LabelKeyAllowlist. " +
                    $"Allowed: [{string.Join(", ", _options.LabelKeyAllowlist)}].");
            }
        }
    }

    private static string ExtractSelectorKey(string clause)
    {
        // Supported shapes (subset of Kubernetes label selector grammar):
        //   key
        //   !key
        //   key=value
        //   key==value
        //   key!=value
        //   key in (v1,v2)
        //   key notin (v1,v2)
        var working = clause.TrimStart('!');
        var inIdx = working.IndexOf(" in ", StringComparison.Ordinal);
        var notInIdx = working.IndexOf(" notin ", StringComparison.Ordinal);
        if (notInIdx > 0) return working.Substring(0, notInIdx).Trim();
        if (inIdx > 0) return working.Substring(0, inIdx).Trim();

        var eqIdx = working.IndexOf('=');
        var bangIdx = working.IndexOf("!=", StringComparison.Ordinal);
        if (bangIdx >= 0) return working.Substring(0, bangIdx).Trim();
        if (eqIdx >= 0) return working.Substring(0, eqIdx).TrimEnd('=').Trim();
        return working.Trim();
    }

    private int ClampLimit(int requested)
    {
        if (requested <= 0)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.InvalidArgument,
                $"limit must be >= 1 (got {requested}).");
        }
        if (requested > _options.MaxListLimit)
        {
            _logger.LogDebug("Clamping list_pods limit {Requested} to MaxListLimit {Max}.", requested, _options.MaxListLimit);
            return _options.MaxListLimit;
        }
        return requested;
    }

    private static V1Container? SelectContainer(V1Pod pod, string? requested)
    {
        var containers = pod.Spec?.Containers;
        if (containers is null || containers.Count == 0) return null;
        if (string.IsNullOrEmpty(requested)) return containers[0];
        return containers.FirstOrDefault(c => string.Equals(c.Name, requested, StringComparison.Ordinal));
    }

    private static bool IsReady(V1Pod pod)
    {
        var conditions = pod.Status?.Conditions;
        if (conditions is null) return false;
        foreach (var cond in conditions)
        {
            if (string.Equals(cond.Type, "Ready", StringComparison.Ordinal))
            {
                return string.Equals(cond.Status, "True", StringComparison.Ordinal);
            }
        }
        return false;
    }

    private (bool Prepared, string Reason) EvaluatePreparedness(V1Pod pod, V1Container container)
    {
        var labels = pod.Metadata?.Labels;
        if (labels is not null &&
            labels.TryGetValue(_options.PreparedLabelKey, out var labelValue) &&
            string.Equals(labelValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"opt-in label '{_options.PreparedLabelKey}=true'");
        }

        if (_options.RequirePreparedLabel)
        {
            return (false, $"missing opt-in label '{_options.PreparedLabelKey}=true'");
        }

        // Heuristic fallback (only when RequirePreparedLabel = false).
        var hasTmpVolume = container.VolumeMounts?.Any(m =>
            string.Equals(m.MountPath, PreparedHeuristicTmpDir, StringComparison.Ordinal)) == true;
        var hasNonRootUid = pod.Spec?.SecurityContext?.RunAsUser is > 0;
        var hasDiagnosticsEnv = container.Env?.Any(e =>
            string.Equals(e.Name, DotnetEnableDiagnosticsEnvVar, StringComparison.Ordinal) &&
            !string.Equals(e.Value, "0", StringComparison.Ordinal)) == true;

        if (hasTmpVolume && hasNonRootUid && hasDiagnosticsEnv)
        {
            return (true, "heuristic: shared /tmp volume + non-root UID + DOTNET_EnableDiagnostics");
        }

        var missing = new List<string>(3);
        if (!hasTmpVolume) missing.Add("shared /tmp volume mount");
        if (!hasNonRootUid) missing.Add("non-root UID");
        if (!hasDiagnosticsEnv) missing.Add("DOTNET_EnableDiagnostics env var");
        return (false, $"heuristic miss: {string.Join(", ", missing)}");
    }

    private PodCandidate BuildCandidate(V1Pod pod, V1Container container, bool ready, bool prepared, string reason)
    {
        var meta = pod.Metadata;
        var owner = meta?.OwnerReferences?.FirstOrDefault();
        var labels = meta?.Labels is null
            ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()
            : FilterLabels(meta.Labels);

        return new PodCandidate(
            Namespace: meta?.NamespaceProperty ?? string.Empty,
            Name: meta?.Name ?? string.Empty,
            ContainerName: container.Name,
            Phase: pod.Status?.Phase ?? "Unknown",
            Ready: ready,
            CreatedAt: meta?.CreationTimestamp is { } ct ? new DateTimeOffset(ct, TimeSpan.Zero) : null,
            NodeName: pod.Spec?.NodeName,
            OwnerKind: owner?.Kind,
            OwnerName: owner?.Name,
            ImageRef: container.Image,
            Labels: labels,
            DiagnosticsPrepared: prepared,
            PreparationReason: reason,
            ActiveInvestigationCount: 0);
    }

    private Dictionary<string, string> FilterLabels(IDictionary<string, string> source)
    {
        // When LabelKeyAllowlist is configured, surface only those keys back to the caller
        // (and always the prepared key so the LLM can correlate the verdict). When the
        // allowlist is empty (any-key mode) we still trim to a sane upper bound to keep the
        // payload compact.
        const int MaxLabelsWhenUnfiltered = 16;
        if (_options.LabelKeyAllowlist.Count == 0)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in source.Take(MaxLabelsWhenUnfiltered)) dict[kv.Key] = kv.Value;
            return dict;
        }
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in _options.LabelKeyAllowlist)
        {
            if (source.TryGetValue(key, out var value)) allowed[key] = value;
        }
        if (source.TryGetValue(_options.PreparedLabelKey, out var preparedValue) &&
            !allowed.ContainsKey(_options.PreparedLabelKey))
        {
            allowed[_options.PreparedLabelKey] = preparedValue;
        }
        return allowed;
    }
}
