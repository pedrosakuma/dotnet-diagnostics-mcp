using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Client-safe projection of <see cref="IInvestigationStore.Snapshot"/>, returned by the
/// <c>list_active_investigations</c> MCP tool. Each entry is the same shape <c>attach_to_pod</c>
/// returns so the LLM has one investigation envelope to reason about across both tools.
/// </summary>
/// <remarks>
/// Bearer tokens are never included — <see cref="AttachSession.FromHandle"/> drops them
/// at projection time. The orchestrator's reverse proxy reinjects the per-attach Pod-local
/// secret on every forwarded call, so the external client never needs to see it.
/// </remarks>
public sealed record InvestigationListPage(
    IReadOnlyList<AttachSession> Items,
    int TotalKnown,
    int ActiveCount,
    int AttachingCount,
    int ClosedCount,
    int ExpiredCount,
    int FailedCount);
