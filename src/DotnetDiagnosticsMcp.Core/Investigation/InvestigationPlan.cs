namespace DotnetDiagnosticsMcp.Core.Investigation;

/// <summary>How the planner was invoked. Drives which steps and early-stop conditions emit.</summary>
public enum InvestigationMode
{
    /// <summary>No prior context. Start from USE-style vitals and branch on counter evidence.</summary>
    Cold,
    /// <summary>A baseline from a prior investigation is provided; skip steps already covered and compare.</summary>
    Warm,
    /// <summary>The caller already has a hypothesis in plain language; route directly to confirming evidence.</summary>
    Hypothesis,
}

public enum StepStatus
{
    Pending,
    InProgress,
    Done,
    Skipped,
}

/// <summary>Single ready-to-execute tool call inside an investigation plan.</summary>
public sealed record InvestigationStep(
    int StepNumber,
    string StepId,
    string ToolName,
    IReadOnlyDictionary<string, object?> ToolParams,
    string Rationale,
    IReadOnlyList<DecisionBranch> Branches,
    StepStatus Status = StepStatus.Pending,
    string? SkipReason = null);

/// <summary>"If condition holds → run step with id NextStepId." Conditions are evaluated by the LLM.</summary>
public sealed record DecisionBranch(string Condition, string NextStepId, string Description);

/// <summary>Closes the loop early when evidence is conclusive (saves tokens + production load).</summary>
public sealed record EarlyStopCondition(
    string ConditionId,
    string Description,
    string Action,
    string? ReportTemplate = null);

/// <summary>A fork the LLM can take mid-investigation if a different symptom emerges.</summary>
public sealed record AlternateBranch(
    string BranchId,
    string Trigger,
    string NextTool,
    string Description);

/// <summary>Numeric snapshot from a prior investigation, used for delta detection in warm mode.</summary>
public sealed record BaselineHandle(
    string InvestigationId,
    DateTimeOffset SnapshotAt,
    IReadOnlyDictionary<string, double> KeyMetrics,
    IReadOnlyList<BaselineHotspotSummary>? CpuHotspots = null);

public sealed record BaselineHotspotSummary(string Frame, double ExclusivePercent);

/// <summary>What to compare between the new collection and the baseline.</summary>
public sealed record MetricComparison(
    string MetricName,
    double BaselineValue,
    string? Unit = null,
    double? RegressionThresholdPercent = null);

/// <summary>Hard limits surfaced to the LLM so it self-polices loops, dump cost, and per-call durations.</summary>
public sealed record InvestigationConstraints(
    int MaxToolCalls = 8,
    bool DumpRequiresApproval = true,
    string MaxDumpType = "Mini",
    int MaxDurationSecondsPerCall = 30);

/// <summary>Plan returned by <c>start_investigation</c>. Self-describing so the LLM can execute without re-reading docs.</summary>
public sealed record InvestigationPlan(
    string InvestigationId,
    DateTimeOffset CreatedAt,
    InvestigationMode Mode,
    int ProcessId,
    InvestigationStep NextStep,
    IReadOnlyList<InvestigationStep> AllSteps,
    IReadOnlyList<InvestigationTerminal> Terminals,
    IReadOnlyList<EarlyStopCondition> EarlyStopConditions,
    IReadOnlyList<AlternateBranch> AlternateBranches,
    InvestigationConstraints Constraints,
    string? Symptom = null,
    string? Hypothesis = null,
    BaselineHandle? Baseline = null,
    IReadOnlyList<MetricComparison>? BaselineComparisons = null);

/// <summary>
/// Named end-state for the decision tree (e.g. "report-cpu", "dump-heap"). Branches whose
/// <c>NextStepId</c> matches a terminal id are leaves: the LLM should either invoke the embedded
/// tool with the given params (if non-null) or emit the report template directly. Terminals
/// referencing <c>collect_process_dump</c> are always approval-gated regardless of constraints.
/// </summary>
public sealed record InvestigationTerminal(
string TerminalId,
string Action,
string Description,
string? ToolName = null,
IReadOnlyDictionary<string, object?>? ToolParams = null,
string? ReportTemplate = null,
bool RequiresApproval = false);
