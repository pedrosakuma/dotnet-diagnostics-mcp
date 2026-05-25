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
        catch (KubeconfigHandleNotFoundException ex)
        {
            // #234 — distinct error kind so the LLM can react with discover_azure rather
            // than retry the listing. The ex.Message is safe to forward (never contains
            // the handle value); see DefaultKubernetesClientFactory.GetOrBuildHandleClient.
            throw new OrchestratorException(
                OrchestratorErrorKinds.KubeconfigHandleNotFound,
                ex.Message, ex);
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
            // Server-side policy: when RequirePreparedLabel is on, an unprepared pod is
            // never returned regardless of the caller's preparedOnly flag. preparedOnly is
            // an additional caller-side filter that may only narrow, never widen, the set.
            if (!prepared && (_options.RequirePreparedLabel || request.PreparedOnly)) continue;

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

        foreach (var raw in SplitSelectorClauses(labelSelector))
        {
            var clause = raw.Trim();
            if (clause.Length == 0) continue;
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

    private static IEnumerable<string> SplitSelectorClauses(string labelSelector)
    {
        // Paren-aware comma split: 'app in (api,worker),env=prod' -> ['app in (api,worker)', 'env=prod'].
        var depth = 0;
        var start = 0;
        for (var i = 0; i < labelSelector.Length; i++)
        {
            var c = labelSelector[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    throw new OrchestratorException(
                        OrchestratorErrorKinds.SelectorRejected,
                        "Label selector has unbalanced ')'.");
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return labelSelector.Substring(start, i - start);
                start = i + 1;
            }
        }
        if (depth != 0)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.SelectorRejected,
                "Label selector has unbalanced '('.");
        }
        yield return labelSelector.Substring(start);
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
        // Rejects malformed shapes like '!key=value' where the '!' prefix and an '=' op
        // both appear (Kubernetes treats '!' as the existence-negation operator and it
        // is mutually exclusive with equality/set operators).
        var negated = clause.StartsWith('!');
        var working = negated ? clause.Substring(1).TrimStart() : clause;

        var notInIdx = IndexOfKeyword(working, "notin");
        if (notInIdx > 0)
        {
            if (negated) return string.Empty;
            return working.Substring(0, notInIdx).TrimEnd();
        }
        var inIdx = IndexOfKeyword(working, "in");
        if (inIdx > 0)
        {
            if (negated) return string.Empty;
            return working.Substring(0, inIdx).TrimEnd();
        }

        var bangIdx = working.IndexOf("!=", StringComparison.Ordinal);
        if (bangIdx >= 0)
        {
            if (negated) return string.Empty;
            return working.Substring(0, bangIdx).TrimEnd();
        }
        var eqIdx = working.IndexOf('=');
        if (eqIdx >= 0)
        {
            if (negated) return string.Empty;
            return working.Substring(0, eqIdx).TrimEnd('=').TrimEnd();
        }

        return working.Trim();
    }

    private static int IndexOfKeyword(string clause, string keyword)
    {
        // Match ' keyword ' (whitespace-bounded) so we don't false-match a label key
        // like 'application' as containing 'in'.
        var needle = ' ' + keyword + ' ';
        var idx = clause.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? -1 : idx + 1; // return index of the keyword itself, not the leading space
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
