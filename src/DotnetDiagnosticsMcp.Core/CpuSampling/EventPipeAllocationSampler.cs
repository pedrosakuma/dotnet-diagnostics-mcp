using System.Diagnostics.Tracing;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Collects allocation samples by writing a <c>.nettrace</c> to disk then parsing it
/// via <see cref="TraceLog"/>. Uses <c>GCAllocationTick</c> events from
/// <c>Microsoft-Windows-DotNETRuntime</c> (GCKeyword=0x1, Verbose), which fire roughly
/// every 100 KB of total managed allocations and carry the <c>TypeName</c> of the sampled
/// object plus a call stack.
/// </summary>
/// <remarks>
/// <para>Empirical probe against .NET SDK 10.0.201 / CoreCLR: enabling
/// <c>Microsoft-DotNETCore-SampleProfiler</c> with keyword <c>0x80000000</c> still emitted only
/// <c>Thread/Sample</c> CPU samples, not any allocation-specific event. Until the runtime starts
/// surfacing <c>AllocationSampled</c>, <c>GCAllocationTick</c> remains the supported allocation
/// backend.</para>
/// <para><b>CoreCLR</b>: <c>TypeName</c> is fully populated; call stacks resolve to managed
/// method names via TraceEvent rundown. <c>MethodIdentity</c> (MVID + token) is emitted for
/// call-tree frames, enabling the assembly-mcp handoff.</para>
/// <para><b>NativeAOT</b>: the GC fires <c>GCAllocationTick</c> events, but the
/// <c>TypeName</c> field is empty — NativeAOT strips managed type metadata at compile time and
/// does not populate this field. All events will roll up under <c>&lt;unknown&gt;</c>. Call
/// stacks are still captured but contain native frame addresses. The call-tree handle is still
/// registered so the LLM can drill into native allocation sites.</para>
/// <para>The sampling rate is inherent to the GC (~one tick per 100 KB of total allocations);
/// it is not tunable via EventPipe parameters.</para>
/// </remarks>
public sealed class EventPipeAllocationSampler
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>
    /// GCKeyword on <c>Microsoft-Windows-DotNETRuntime</c>. Combined with Verbose level this
    /// enables <c>GCAllocationTick</c> events that fire every ~100 KB of managed allocations.
    /// </summary>
    private const long GcKeyword = 0x1L;

    private readonly ILogger<EventPipeAllocationSampler> _logger;
    private readonly MvidReader _mvidReader;

    public EventPipeAllocationSampler(ILogger<EventPipeAllocationSampler>? logger = null, MvidReader? mvidReader = null)
    {
        _logger = logger ?? NullLogger<EventPipeAllocationSampler>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
    }

    /// <summary>
    /// Samples allocations in the target process for <paramref name="duration"/> and
    /// aggregates the captured <c>GCAllocationTick</c> events into a top-N type summary plus
    /// a merged call-tree artifact suitable for follow-up drill-down via <c>get_call_tree</c>.
    /// </summary>
    public async Task<AllocationSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }

        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }

        var tracePath = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-alloc-{processId}-{Guid.NewGuid():N}.nettrace");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await CollectTraceAsync(processId, tracePath, duration, cancellationToken).ConfigureAwait(false);
            var (summary, artifact) = Aggregate(tracePath, processId, startedAt, duration, topN);
            return new AllocationSampleResult(summary, artifact);
        }
        finally
        {
            TryDelete(tracePath);
        }
    }

    private static async Task CollectTraceAsync(int pid, string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var providers = new[]
        {
            // Verbose GCKeyword enables GCAllocationTick (fires every ~100 KB of managed allocations).
            // Default DotNETRuntime keywords supply the rundown events needed to resolve managed
            // method names and IL tokens when the nettrace is re-opened via TraceLog.
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose,
                GcKeyword | (long)ClrTraceEventParser.Keywords.Default),
        };

        var client = new DiagnosticsClient(pid);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: 256, ct)
            .ConfigureAwait(false);

        var copyTask = Task.Run(async () =>
        {
            await using var output = File.Create(outputPath);
            await session.EventStream.CopyToAsync(output, ct).ConfigureAwait(false);
        }, ct);

        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await copyTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }
    }

    private (AllocationSample Summary, CpuSampleTraceArtifact Artifact) Aggregate(
        string tracePath,
        int pid,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN)
    {
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var process = traceLog.Processes.LastProcessWithID(pid);
            if (process is null)
            {
                _logger.LogDebug("Process {Pid} not found in trace.", pid);
                return (EmptySummary(pid, startedAt, duration), EmptyArtifact(pid, startedAt, duration));
            }

            // Per-type byte/event accumulators.
            var byType = new Dictionary<string, TypeAccumulator>(StringComparer.Ordinal);

            // Merged allocation call tree (all types combined, weighted by byte amount).
            var rootBuilder = new CallTreeBuilder();
            var modules = new Dictionary<string, string>(StringComparer.Ordinal);
            // Maps composite frame key (module!methodDisplay) → TraceCodeAddress for MethodIdentity extraction.
            var traceCodeAddressByFrameKey = new Dictionary<string, TraceCodeAddress>(StringComparer.Ordinal);

            long totalEvents = 0;
            long totalBytes = 0;

            foreach (var traceEvent in process.EventsInProcess)
            {
                if (traceEvent.EventName != "GC/AllocationTick")
                {
                    continue;
                }

                var typeName = traceEvent.PayloadByName("TypeName") as string;
                if (string.IsNullOrEmpty(typeName))
                {
                    // NativeAOT: TypeName field is empty. Preserve the event for count/bytes
                    // aggregation but group under the sentinel so the LLM can see the total load.
                    typeName = "<unknown>";
                }

                var amountObj = traceEvent.PayloadByName("AllocationAmount64");
                var bytes = amountObj switch
                {
                    ulong ul => (long)ul,
                    long l => l,
                    uint ui => (long)ui,
                    int i => (long)i,
                    _ => 0L,
                };

                var kindObj = traceEvent.PayloadByName("AllocationKind");
                var kind = kindObj switch
                {
                    uint ui => ui == 1 ? HeapKind.Large : HeapKind.Small,
                    int i => i == 1 ? HeapKind.Large : HeapKind.Small,
                    _ => HeapKind.Small,
                };

                totalEvents++;
                totalBytes += bytes;

                if (!byType.TryGetValue(typeName, out var acc))
                {
                    acc = new TypeAccumulator(typeName);
                    byType[typeName] = acc;
                }
                acc.Add(bytes, kind);

                // Build the merged allocation call tree.
                var callStack = traceEvent.CallStack();
                if (callStack is not null)
                {
                    var stackFrames = new List<(string Key, string Module, string Display)>();
                    var frame = callStack;
                    while (frame is not null)
                    {
                        var display = FormatFrame(frame);
                        var module = frame.CodeAddress?.ModuleFile?.Name ?? string.Empty;
                        var key = string.IsNullOrEmpty(module) ? display : module + "!" + display;
                        stackFrames.Add((key, module, display));
                        modules.TryAdd(key, module);
                        if (frame.CodeAddress is not null)
                        {
                            traceCodeAddressByFrameKey.TryAdd(key, frame.CodeAddress);
                        }
                        frame = frame.Caller;
                    }

                    // stack is leaf→root; reverse to root→leaf for tree traversal.
                    stackFrames.Reverse();
                    if (stackFrames.Count > 0)
                    {
                        rootBuilder.AddStack(stackFrames, stackFrames[^1].Key);
                    }
                }
            }

            var topByBytes = byType.Values
                .OrderByDescending(a => a.TotalBytes)
                .Take(topN)
                .Select(a => a.ToRecord())
                .ToList();

            var topByCount = byType.Values
                .OrderByDescending(a => a.EventCount)
                .Take(topN)
                .Select(a => a.ToRecord())
                .ToList();

            // Build MethodIdentity for every symbolized frame in the call tree (CoreCLR only;
            // NativeAOT frames won't have IL tokens but the frame names are still surfaced).
            var ranked = modules.Keys
                .Where(k => traceCodeAddressByFrameKey.ContainsKey(k))
                .Select(k => new KeyValuePair<string, long>(k, 0))
                .ToArray();

            var identities = BuildMethodIdentities(ranked, modules, traceCodeAddressByFrameKey);

            var summary = new AllocationSample(pid, startedAt, duration, totalEvents, totalBytes, topByBytes, topByCount);
            var artifact = new CpuSampleTraceArtifact(
                pid, startedAt, duration, totalEvents,
                rootBuilder.Build(),
                ResolvedSources: null,
                MethodIdentities: identities);
            return (summary, artifact);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private Dictionary<SymbolRef, MethodIdentity> BuildMethodIdentities(
        KeyValuePair<string, long>[] ranked,
        Dictionary<string, string> modules,
        Dictionary<string, TraceCodeAddress> traceCodeAddressByFrameKey)
    {
        var result = new Dictionary<SymbolRef, MethodIdentity>();
        foreach (var (key, _) in ranked)
        {
            if (!traceCodeAddressByFrameKey.TryGetValue(key, out var addr)) continue;
            var method = addr.Method;
            if (method is null) continue;

            var moduleFile = method.MethodModuleFile;
            var modulePath = moduleFile?.FilePath;
            var moduleName = !string.IsNullOrEmpty(modulePath)
                ? Path.GetFileName(modulePath)
                : (moduleFile?.Name is { Length: > 0 } n ? n : modules.GetValueOrDefault(key, string.Empty));

            var token = method.MethodToken;
            var parsed = EventPipeCpuSampler.ParseFullMethodName(method.FullMethodName);
            var mvid = _mvidReader.TryRead(modulePath);

            if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleName))
            {
                continue;
            }

            var module = modules.GetValueOrDefault(key, moduleName ?? string.Empty);
            var display = !string.IsNullOrEmpty(module) && key.StartsWith(module + "!", StringComparison.Ordinal)
                ? key[(module.Length + 1)..]
                : key;
            var symbol = new SymbolRef(module, display);
            result[symbol] = new MethodIdentity(
                ModuleName: moduleName,
                ModulePath: modulePath,
                ModuleVersionId: mvid,
                MetadataToken: token > 0 ? token : null,
                TypeFullName: parsed.TypeFullName,
                MethodName: parsed.MethodName,
                GenericArity: parsed.GenericArity)
            {
                GenericTypeArguments = parsed.GenericTypeArguments,
            };
        }
        return result;
    }

    private static string FormatFrame(TraceCallStack frame)
    {
        var address = frame.CodeAddress;
        if (address?.Method is { } method)
        {
            return method.FullMethodName;
        }

        if (address?.ModuleFile is { } module)
        {
            return $"{module.Name}!0x{address.Address:x}";
        }

        return $"0x{address?.Address ?? 0:x}";
    }

    private static AllocationSample EmptySummary(int pid, DateTimeOffset startedAt, TimeSpan duration)
        => new(pid, startedAt, duration, 0, 0, Array.Empty<AllocatedType>(), Array.Empty<AllocatedType>());

    private static CpuSampleTraceArtifact EmptyArtifact(int pid, DateTimeOffset startedAt, TimeSpan duration)
        => new(pid, startedAt, duration, 0, CreateEmptyRootNode());

    /// <summary>Returns an empty call tree root node used when no events were collected.</summary>
    private static CallTreeNode CreateEmptyRootNode()
        => new(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private sealed class TypeAccumulator
    {
        private long _totalBytes;
        private long _eventCount;
        private long _smallCount;
        private long _largeCount;

        public TypeAccumulator(string typeName) => TypeName = typeName;

        public string TypeName { get; }
        public long TotalBytes => _totalBytes;
        public long EventCount => _eventCount;

        public void Add(long bytes, HeapKind kind)
        {
            _totalBytes += bytes;
            _eventCount++;
            if (kind == HeapKind.Large) _largeCount++; else _smallCount++;
        }

        public AllocatedType ToRecord()
            => new(
                TypeName,
                _totalBytes,
                _eventCount,
                _largeCount > _smallCount ? HeapKind.Large : HeapKind.Small,
                new TypeIdentity(TypeName));
    }
}
