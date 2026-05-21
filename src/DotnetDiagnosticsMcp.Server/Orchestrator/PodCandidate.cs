using System;
using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// A single Pod surfaced by <c>list_pods</c> as a candidate for diagnostic attach.
/// Mirrors the <c>PodCandidate</c> shape from docs/central-orchestrator-design.md §3.3.
/// </summary>
/// <param name="Namespace">Kubernetes namespace of the Pod.</param>
/// <param name="Name">Pod name.</param>
/// <param name="ContainerName">
/// The application container the orchestrator would target with <c>--target</c> when
/// injecting an ephemeral debug container. Defaults to the first container in the spec
/// when the caller does not specify one.
/// </param>
/// <param name="Phase">Pod phase (<c>Pending</c> / <c>Running</c> / <c>Succeeded</c> / <c>Failed</c> / <c>Unknown</c>).</param>
/// <param name="Ready">True when every container reports Ready.</param>
/// <param name="CreatedAt">Pod creation timestamp (UTC, may be null while server-side defaulting is in flight).</param>
/// <param name="NodeName">Scheduled node name. Null for pending Pods.</param>
/// <param name="OwnerKind">Owner reference kind (e.g. <c>ReplicaSet</c>, <c>StatefulSet</c>, <c>DaemonSet</c>). Null when no owner exists.</param>
/// <param name="OwnerName">Owner reference name. Null when no owner exists.</param>
/// <param name="ImageRef">Image reference of the targeted container.</param>
/// <param name="Labels">Subset of Pod labels surfaced to the caller (filtered by the orchestrator's label allowlist when configured).</param>
/// <param name="DiagnosticsPrepared">True when the Pod is considered diagnostically prepared (label opt-in or heuristic match).</param>
/// <param name="PreparationReason">Short human-readable string explaining the preparedness verdict.</param>
/// <param name="ActiveInvestigationCount">Number of active investigations the orchestrator currently has against this Pod. Always 0 in P3a (no attach yet).</param>
public sealed record PodCandidate(
    string Namespace,
    string Name,
    string ContainerName,
    string Phase,
    bool Ready,
    DateTimeOffset? CreatedAt,
    string? NodeName,
    string? OwnerKind,
    string? OwnerName,
    string? ImageRef,
    IReadOnlyDictionary<string, string> Labels,
    bool DiagnosticsPrepared,
    string PreparationReason,
    int ActiveInvestigationCount);

/// <summary>
/// Page of <see cref="PodCandidate"/> rows plus an optional opaque continuation cursor.
/// </summary>
public sealed record PodCandidatePage(
    IReadOnlyList<PodCandidate> Items,
    string? NextCursor);
