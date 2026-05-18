namespace DotnetDiagnosticsMcp.Core.Investigation;

/// <summary>
/// Builds a structured <see cref="InvestigationPlan"/> for the meta-tool <c>start_investigation</c>.
/// Implementations are pure (no I/O) — the plan is deterministic given inputs so the LLM and tests
/// can reason about routing without spinning up diagnostic sessions.
/// </summary>
public interface IInvestigationPlanner
{
    InvestigationPlan Plan(InvestigationRequest request);
}

/// <summary>Inputs to <see cref="IInvestigationPlanner.Plan"/>.</summary>
public sealed record InvestigationRequest(
    int ProcessId,
    string? Symptom = null,
    string? Hypothesis = null,
    BaselineHandle? Baseline = null,
    InvestigationConstraints? Constraints = null);
