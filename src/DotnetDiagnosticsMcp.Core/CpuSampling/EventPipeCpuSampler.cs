using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Collects a CPU sample by writing a <c>.nettrace</c> to disk, then parsing it via
/// <see cref="TraceLog"/> to produce top-N hotspot aggregations. Requires CoreCLR
/// (the SampleProfiler provider is not implemented in NativeAOT).
/// </summary>
public sealed class EventPipeCpuSampler : ICpuSampler
{
    private readonly ILogger<EventPipeCpuSampler> _logger;

    public EventPipeCpuSampler(ILogger<EventPipeCpuSampler>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeCpuSampler>.Instance;
    }

    public async Task<CpuSampleResult> SampleAsync(
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

        var tracePath = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-{processId}-{Guid.NewGuid():N}.nettrace");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await CollectTraceAsync(processId, tracePath, duration, cancellationToken).ConfigureAwait(false);
            var (total, hotspots, root) = AggregateHotspots(tracePath, processId, topN);
            var summary = new CpuSample(processId, startedAt, duration, total, hotspots);
            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, total, root);
            return new CpuSampleResult(summary, artifact);
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
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Informational,
                (long)ClrTraceEventParser.Keywords.Default),
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
            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort stop
            }

            try
            {
                await copyTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort
            }

            session.Dispose();
        }
    }

    private (long Total, IReadOnlyList<Hotspot> Hotspots, CallTreeNode Root) AggregateHotspots(string tracePath, int pid, int topN)
    {
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var process = traceLog.Processes.LastProcessWithID(pid);
            if (process is null)
            {
                _logger.LogDebug("Process {Pid} not found in trace.", pid);
                return (0, Array.Empty<Hotspot>(), EmptyRoot());
            }

            var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
            var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
            var modules = new Dictionary<string, string>(StringComparer.Ordinal);
            var rootBuilder = new CallTreeBuilder();
            long total = 0;

            foreach (var traceEvent in process.EventsInProcess)
            {
                if (traceEvent.ProviderName != "Microsoft-DotNETCore-SampleProfiler" ||
                    traceEvent.EventName != "Thread/Sample")
                {
                    continue;
                }

                var callStack = traceEvent.CallStack();
                if (callStack is null)
                {
                    continue;
                }

                total++;
                var stackFrames = new List<(string Key, string Module)>();
                var frame = callStack;
                while (frame is not null)
                {
                    var key = FormatFrame(frame);
                    var module = frame.CodeAddress?.ModuleFile?.Name ?? string.Empty;
                    stackFrames.Add((key, module));
                    modules.TryAdd(key, module);
                    frame = frame.Caller;
                }

                // stack is leaf→root; reverse to root→leaf for tree traversal.
                stackFrames.Reverse();

                var leafKey = stackFrames[^1].Key;
                exclusive[leafKey] = exclusive.GetValueOrDefault(leafKey) + 1;

                var seenInThisStack = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, _) in stackFrames)
                {
                    if (seenInThisStack.Add(key))
                    {
                        inclusive[key] = inclusive.GetValueOrDefault(key) + 1;
                    }
                }

                rootBuilder.AddStack(stackFrames, leafKey);
            }

            var hotspots = inclusive
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => new Hotspot(
                    Frame: new SampledFrame(
                        Module: modules.GetValueOrDefault(kv.Key, string.Empty),
                        Method: kv.Key),
                    InclusiveSamples: kv.Value,
                    ExclusiveSamples: exclusive.GetValueOrDefault(kv.Key)))
                .ToList();

            return (total, hotspots, rootBuilder.Build());
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static CallTreeNode EmptyRoot() => new(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());

    private sealed class CallTreeBuilder
    {
        private readonly Node _root = new(new SampledFrame(string.Empty, "<root>"));

        public void AddStack(List<(string Key, string Module)> rootToLeaf, string leafKey)
        {
            var current = _root;
            current.Inclusive++;
            for (var i = 0; i < rootToLeaf.Count; i++)
            {
                var (key, module) = rootToLeaf[i];
                if (!current.Children.TryGetValue(key, out var child))
                {
                    child = new Node(new SampledFrame(module, key));
                    current.Children[key] = child;
                }
                child.Inclusive++;
                if (key == leafKey && i == rootToLeaf.Count - 1)
                {
                    child.Exclusive++;
                }
                current = child;
            }
        }

        public CallTreeNode Build() => Materialize(_root);

        private static CallTreeNode Materialize(Node n)
        {
            var children = n.Children.Values
                .OrderByDescending(c => c.Inclusive)
                .Select(Materialize)
                .ToList();
            return new CallTreeNode(n.Frame, n.Inclusive, n.Exclusive, children);
        }

        private sealed class Node
        {
            public Node(SampledFrame frame) { Frame = frame; }
            public SampledFrame Frame { get; }
            public long Inclusive;
            public long Exclusive;
            public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
        }
    }

    private static string FormatFrame(TraceCallStack frame)
    {
        var address = frame.CodeAddress;
        if (address?.Method is { } method)
        {
            return $"{method.FullMethodName}";
        }

        if (address?.ModuleFile is { } module)
        {
            return $"{module.Name}!0x{address.Address:x}";
        }

        return $"0x{address?.Address ?? 0:x}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }
}
