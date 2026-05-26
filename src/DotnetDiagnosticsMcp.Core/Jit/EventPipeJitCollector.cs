using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Jit;

/// <summary>
/// Captures CLR JIT / tiered-compilation activity from the runtime EventPipe provider.
/// </summary>
public sealed class EventPipeJitCollector : IJitCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long JitKeyword = 0x10;
    private const long JitTracingKeyword = 0x40000;
    private const long JittedMethodILToNativeMapKeyword = 0x20000;
    private const long CompilationDiagnosticKeyword = 0x2000000000;
    private const long JitKeywords =
        JitKeyword |
        JitTracingKeyword |
        JittedMethodILToNativeMapKeyword |
        CompilationDiagnosticKeyword;

    private readonly ILogger<EventPipeJitCollector> _logger;

    public EventPipeJitCollector(ILogger<EventPipeJitCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeJitCollector>.Instance;
    }

    public async Task<JitSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, JitKeywords),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 128, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);
        var pendingStarts = new Dictionary<long, Queue<DateTimeOffset>>();
        var methods = new Dictionary<string, JitMethodAccumulator>(StringComparer.Ordinal);
        var methodKeyById = new Dictionary<long, string>();
        var methodIdsWithIlMap = new HashSet<long>();
        var r2rMissedMethods = new HashSet<long>();
        var r2rMissThenJittedMethods = new HashSet<long>();

        var jitStarts = 0;
        var completedCompilations = 0;
        var tier0Count = 0;
        var tier1Count = 0;
        var readyToRunCount = 0;
        var r2rLookups = 0;
        var r2rHits = 0;
        var reJitCount = 0;
        var osrCount = 0;
        var ilMapCount = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Clr.MethodJittingStarted += data =>
                {
                    jitStarts++;
                    if (!pendingStarts.TryGetValue(data.MethodID, out var queue))
                    {
                        queue = new Queue<DateTimeOffset>();
                        pendingStarts[data.MethodID] = queue;
                    }

                    queue.Enqueue(ToUtcOffset(data.TimeStamp));
                };

                source.Clr.MethodLoadVerbose += data =>
                {
                    completedCompilations++;
                    var completedAt = ToUtcOffset(data.TimeStamp);
                    var started = completedAt;
                    if (pendingStarts.TryGetValue(data.MethodID, out var queue) && queue.Count > 0)
                    {
                        started = queue.Dequeue();
                    }
                    else
                    {
                        notes.Add("Some MethodLoadVerbose events arrived without a preceding MethodJittingStarted event; their inclusive JIT time was recorded as 0ms.");
                    }

                    var inclusiveMs = Math.Max(0, (completedAt - started).TotalMilliseconds);
                    var key = BuildMethodKey(data.MethodNamespace, data.MethodName, data.MethodSignature);
                    if (!methods.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new JitMethodAccumulator(data.MethodNamespace ?? string.Empty, data.MethodName ?? "(unknown)", data.MethodSignature ?? string.Empty);
                        methods[key] = accumulator;
                    }

                    var hasIlMap = methodIdsWithIlMap.Contains(data.MethodID);
                    accumulator.Record(inclusiveMs, data.OptimizationTier, data.ReJITID, hasIlMap);
                    methodKeyById[data.MethodID] = key;

                    switch (ClassifyTier(data.OptimizationTier))
                    {
                        case TierBucket.Tier0:
                            tier0Count++;
                            break;
                        case TierBucket.Tier1:
                            tier1Count++;
                            break;
                        case TierBucket.ReadyToRun:
                            readyToRunCount++;
                            break;
                    }

                    if (data.ReJITID > 0)
                    {
                        reJitCount++;
                    }

                    if (data.OptimizationTier == OptimizationTier.OptimizedTier1OSR)
                    {
                        osrCount++;
                    }

                    if (r2rMissedMethods.Contains(data.MethodID))
                    {
                        r2rMissThenJittedMethods.Add(data.MethodID);
                    }
                };

                source.Clr.MethodILToNativeMap += data =>
                {
                    ilMapCount++;
                    methodIdsWithIlMap.Add(data.MethodID);
                    if (methodKeyById.TryGetValue(data.MethodID, out var key) && methods.TryGetValue(key, out var accumulator))
                    {
                        accumulator.MarkIlMap();
                    }
                };

                source.Clr.MethodR2RGetEntryPointStart += _ =>
                {
                    // Start events do not expose the outcome; the paired stop carries EntryPoint=0
                    // on miss. Subscribing keeps the collector aligned with the runtime lookup flow.
                };

                source.Clr.MethodR2RGetEntryPoint += data =>
                {
                    r2rLookups++;
                    if (data.EntryPoint != 0)
                    {
                        r2rHits++;
                    }
                    else
                    {
                        r2rMissedMethods.Add(data.MethodID);
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe JIT source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        var orderedMethods = methods.Values
            .Select(static accumulator => accumulator.ToRecord())
            .OrderByDescending(static method => method.InclusiveJitTimeMs)
            .ThenByDescending(static method => method.CompilationCount)
            .ThenBy(static method => method.DisplayName, StringComparer.Ordinal)
            .ToList();

        var unresolvedStarts = pendingStarts.Sum(static entry => entry.Value.Count);
        if (unresolvedStarts > 0)
        {
            notes.Add($"{unresolvedStarts} MethodJittingStarted event(s) did not complete before the collection window ended.");
        }

        if (ilMapCount == 0)
        {
            notes.Add("No MethodILToNativeMap events were observed during the window.");
        }

        var distribution = new JitTierDistribution(
            Tier0: tier0Count,
            Tier1: tier1Count,
            ReadyToRun: readyToRunCount,
            R2RHit: r2rHits,
            R2RMissThenJit: r2rMissThenJittedMethods.Count);

        var tierDenominator = Math.Max(1, completedCompilations);
        var tier1Percent = completedCompilations == 0
            ? 0d
            : (tier1Count * 100d) / tierDenominator;
        double? r2rHitRatePercent = r2rLookups == 0
            ? null
            : (r2rHits * 100d) / r2rLookups;
        var healthCheck = completedCompilations == 0 && r2rLookups == 0
            ? "No JIT or ReadyToRun lookups were captured in the window."
            : $"{tier1Percent:F0}% of completed methods reached Tier1; R2R hit rate {(r2rHitRatePercent.HasValue ? $"{r2rHitRatePercent.Value:F0}%" : "n/a")}.";

        return new JitSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            JitStartCount: jitStarts,
            CompletedCompilations: completedCompilations,
            UniqueMethods: orderedMethods.Count,
            Distribution: distribution,
            R2RLookupCount: r2rLookups,
            ReJitCount: reJitCount,
            OsrCount: osrCount,
            IlMapCount: ilMapCount,
            Tier1Percent: tier1Percent,
            R2RHitRatePercent: r2rHitRatePercent,
            HealthCheck: healthCheck,
            Methods: orderedMethods,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static DateTimeOffset ToUtcOffset(DateTime timestamp) =>
        new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static string BuildMethodKey(string? methodNamespace, string? methodName, string? methodSignature)
    {
        var displayName = BuildDisplayName(methodNamespace, methodName, methodSignature);
        return string.IsNullOrWhiteSpace(displayName) ? "(unknown)" : displayName;
    }

    private static string BuildDisplayName(string? methodNamespace, string? methodName, string? methodSignature)
    {
        var name = string.IsNullOrWhiteSpace(methodName) ? "(unknown)" : methodName.Trim();
        var qualifiedName = string.IsNullOrWhiteSpace(methodNamespace)
            ? name
            : $"{methodNamespace.Trim()}.{name}";
        return string.IsNullOrWhiteSpace(methodSignature)
            ? qualifiedName
            : $"{qualifiedName}{methodSignature.Trim()}";
    }

    private static TierBucket ClassifyTier(OptimizationTier tier) => tier switch
    {
        OptimizationTier.MinOptJitted or OptimizationTier.QuickJitted or OptimizationTier.QuickJittedInstrumented => TierBucket.Tier0,
        OptimizationTier.Optimized or OptimizationTier.OptimizedTier1 or OptimizationTier.OptimizedTier1OSR or OptimizationTier.OptimizedTier1Instrumented => TierBucket.Tier1,
        OptimizationTier.ReadyToRun or OptimizationTier.PreJIT => TierBucket.ReadyToRun,
        _ => TierBucket.Unknown,
    };

    private enum TierBucket
    {
        Unknown,
        Tier0,
        Tier1,
        ReadyToRun,
    }

    private sealed class JitMethodAccumulator
    {
        public JitMethodAccumulator(string methodNamespace, string methodName, string methodSignature)
        {
            MethodNamespace = methodNamespace;
            MethodName = methodName;
            MethodSignature = methodSignature;
            DisplayName = BuildDisplayName(methodNamespace, methodName, methodSignature);
        }

        public string MethodNamespace { get; }

        public string MethodName { get; }

        public string MethodSignature { get; }

        public string DisplayName { get; }

        public double InclusiveJitTimeMs { get; private set; }

        public int CompilationCount { get; private set; }

        public string LastOptimizationTier { get; private set; } = OptimizationTier.Unknown.ToString();

        public int Tier0Count { get; private set; }

        public int Tier1Count { get; private set; }

        public int ReadyToRunCount { get; private set; }

        public int ReJitCount { get; private set; }

        public int OsrCount { get; private set; }

        public bool HasIlMap { get; private set; }

        public void Record(double inclusiveJitTimeMs, OptimizationTier tier, long reJitId, bool hasIlMap)
        {
            InclusiveJitTimeMs += inclusiveJitTimeMs;
            CompilationCount++;
            LastOptimizationTier = tier.ToString();
            HasIlMap |= hasIlMap;

            switch (ClassifyTier(tier))
            {
                case TierBucket.Tier0:
                    Tier0Count++;
                    break;
                case TierBucket.Tier1:
                    Tier1Count++;
                    break;
                case TierBucket.ReadyToRun:
                    ReadyToRunCount++;
                    break;
            }

            if (reJitId > 0)
            {
                ReJitCount++;
            }

            if (tier == OptimizationTier.OptimizedTier1OSR)
            {
                OsrCount++;
            }
        }

        public void MarkIlMap() => HasIlMap = true;

        public JitMethodSummary ToRecord() => new(
            MethodNamespace,
            MethodName,
            MethodSignature,
            DisplayName,
            Math.Round(InclusiveJitTimeMs, 3),
            CompilationCount,
            LastOptimizationTier,
            Tier0Count,
            Tier1Count,
            ReadyToRunCount,
            ReJitCount,
            OsrCount,
            HasIlMap);
    }
}
