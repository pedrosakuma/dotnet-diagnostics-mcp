namespace DotnetDiagnosticsMcp.Core.Jit;

/// <summary>
/// Aggregated JIT / tiered-compilation activity observed during a collection window.
/// </summary>
public sealed record JitSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int JitStartCount,
    int CompletedCompilations,
    int UniqueMethods,
    JitTierDistribution Distribution,
    int R2RLookupCount,
    int ReJitCount,
    int OsrCount,
    int IlMapCount,
    double Tier1Percent,
    double? R2RHitRatePercent,
    string HealthCheck,
    IReadOnlyList<JitMethodSummary> Methods,
    IReadOnlyList<string> Notes);

/// <summary>
/// Tier distribution and ReadyToRun outcomes reconstructed from CLR method load / R2R lookup events.
/// </summary>
public sealed record JitTierDistribution(
    int Tier0,
    int Tier1,
    int ReadyToRun,
    int R2RHit,
    int R2RMissThenJit);

/// <summary>
/// Aggregated JIT information for a single method identity (namespace + name + signature).
/// </summary>
public sealed record JitMethodSummary(
    string MethodNamespace,
    string MethodName,
    string MethodSignature,
    string DisplayName,
    double InclusiveJitTimeMs,
    int CompilationCount,
    string LastOptimizationTier,
    int Tier0Count,
    int Tier1Count,
    int ReadyToRunCount,
    int ReJitCount,
    int OsrCount,
    bool HasIlMap);
