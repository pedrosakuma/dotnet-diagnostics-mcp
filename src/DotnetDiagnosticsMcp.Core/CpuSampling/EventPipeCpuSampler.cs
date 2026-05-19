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
    private readonly MvidReader _mvidReader;

    public EventPipeCpuSampler(ILogger<EventPipeCpuSampler>? logger = null, MvidReader? mvidReader = null)
    {
        _logger = logger ?? NullLogger<EventPipeCpuSampler>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
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
            var (total, hotspots, root, sources, identities) = AggregateHotspots(tracePath, processId, topN, sourceResolution);
            var summary = new CpuSample(processId, startedAt, duration, total, hotspots);
            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, total, root, sources, identities);
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

    private (long Total, IReadOnlyList<Hotspot> Hotspots, CallTreeNode Root, IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation>? Sources, IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity>? Identities) AggregateHotspots(string tracePath, int pid, int topN, SourceResolutionOptions? sourceResolution)
    {
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var process = traceLog.Processes.LastProcessWithID(pid);
            if (process is null)
            {
                _logger.LogDebug("Process {Pid} not found in trace.", pid);
                return (0, Array.Empty<Hotspot>(), EmptyRoot(), null, null);
            }

            var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
            var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
            var modules = new Dictionary<string, string>(StringComparer.Ordinal);
            var codeAddressByKey = new Dictionary<string, Microsoft.Diagnostics.Tracing.Etlx.TraceCodeAddress>(StringComparer.Ordinal);
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
                var stackFrames = new List<(string Key, string Module, string Display)>();
                var frame = callStack;
                while (frame is not null)
                {
                    var display = FormatFrame(frame);
                    var module = frame.CodeAddress?.ModuleFile?.Name ?? string.Empty;
                    // Aggregate by (module, methodName) — two distinct methods in different
                    // modules can share FullMethodName and we must not merge them, or we'd
                    // hand the assembly MCP an identity from the wrong module.
                    var key = string.IsNullOrEmpty(module) ? display : module + "!" + display;
                    stackFrames.Add((key, module, display));
                    modules.TryAdd(key, module);
                    if (frame.CodeAddress is not null)
                    {
                        codeAddressByKey.TryAdd(key, frame.CodeAddress);
                    }
                    frame = frame.Caller;
                }

                // stack is leaf→root; reverse to root→leaf for tree traversal.
                stackFrames.Reverse();

                var leafKey = stackFrames[^1].Key;
                exclusive[leafKey] = exclusive.GetValueOrDefault(leafKey) + 1;

                var seenInThisStack = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, _, _) in stackFrames)
                {
                    if (seenInThisStack.Add(key))
                    {
                        inclusive[key] = inclusive.GetValueOrDefault(key) + 1;
                    }
                }

                // CallTreeBuilder still wants frames — pass module + display name so the
                // tree shows clean method names (the composite key is internal only).
                var treeFrames = new List<(string Key, string Module, string Display)>(stackFrames.Count);
                foreach (var (k, m, d) in stackFrames) treeFrames.Add((k, m, d));
                rootBuilder.AddStack(treeFrames, leafKey);
            }

            var ranked = inclusive
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .ToArray();

            IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation>? sources = null;
            if (sourceResolution is { Enabled: true })
            {
                sources = ResolveSources(traceLog, ranked, modules, codeAddressByKey, sourceResolution);
            }

            var identities = BuildMethodIdentities(ranked, modules, codeAddressByKey);

            var hotspots = ranked
                .Select(kv =>
                {
                    var module = modules.GetValueOrDefault(kv.Key, string.Empty);
                    var methodDisplay = !string.IsNullOrEmpty(module) && kv.Key.StartsWith(module + "!", StringComparison.Ordinal)
                        ? kv.Key[(module.Length + 1)..]
                        : kv.Key;
                    var symbol = new DotnetDiagnosticsMcp.Core.Memory.SymbolRef(module, methodDisplay);
                    identities.TryGetValue(symbol, out var identity);
                    return new Hotspot(
                        Frame: new SampledFrame(Module: module, Method: methodDisplay),
                        InclusiveSamples: kv.Value,
                        ExclusiveSamples: exclusive.GetValueOrDefault(kv.Key),
                        Identity: identity);
                })
                .ToList();

            return (total, hotspots, rootBuilder.Build(), sources, identities);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity> BuildMethodIdentities(
        KeyValuePair<string, long>[] ranked,
        Dictionary<string, string> modules,
        Dictionary<string, Microsoft.Diagnostics.Tracing.Etlx.TraceCodeAddress> codeAddressByKey)
    {
        var result = new Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity>();
        foreach (var (key, _) in ranked)
        {
            if (!codeAddressByKey.TryGetValue(key, out var addr)) continue;
            var method = addr.Method;
            if (method is null) continue;

            var moduleFile = method.MethodModuleFile;
            var modulePath = moduleFile?.FilePath;
            var moduleName = !string.IsNullOrEmpty(modulePath)
                ? Path.GetFileName(modulePath)
                : (moduleFile?.Name is { Length: > 0 } n ? n : modules.GetValueOrDefault(key, string.Empty));

            var token = method.MethodToken;
            var (typeFull, methodName, arity) = SplitFullMethodName(method.FullMethodName);
            var mvid = _mvidReader.TryRead(modulePath);

            // Skip frames where we have nothing useful for the handoff at all
            // (e.g. native frames with no module path and no IL token).
            if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleName))
            {
                continue;
            }

            var module = modules.GetValueOrDefault(key, moduleName ?? string.Empty);
            var methodDisplay = !string.IsNullOrEmpty(module) && key.StartsWith(module + "!", StringComparison.Ordinal)
                ? key[(module.Length + 1)..]
                : key;
            var symbol = new DotnetDiagnosticsMcp.Core.Memory.SymbolRef(module, methodDisplay);
            result[symbol] = new DotnetDiagnosticsMcp.Core.Memory.MethodIdentity(
                ModuleName: moduleName,
                ModulePath: modulePath,
                ModuleVersionId: mvid,
                MetadataToken: token > 0 ? token : null,
                TypeFullName: typeFull,
                MethodName: methodName,
                GenericArity: arity);
        }
        return result;
    }

    /// <summary>
    /// Splits a TraceEvent <c>FullMethodName</c> (shape: <c>Namespace.Type.Method</c>; generic
    /// methods may surface as <c>Type.Method&lt;T&gt;</c> depending on the runtime) into the
    /// (typeFullName, methodName, genericArity) tuple consumed by the handoff contract.
    /// </summary>
    internal static (string? TypeFullName, string MethodName, int GenericArity) SplitFullMethodName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return (null, string.Empty, 0);

        var name = fullName!;
        var arity = 0;
        var genericOpen = name.IndexOf('<', StringComparison.Ordinal);
        if (genericOpen >= 0)
        {
            var genericClose = name.LastIndexOf('>');
            if (genericClose > genericOpen)
            {
                var genericArgs = name.Substring(genericOpen + 1, genericClose - genericOpen - 1);
                arity = CountTopLevelCommas(genericArgs) + 1;
                name = name.Remove(genericOpen, genericClose - genericOpen + 1);
            }
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == name.Length - 1)
        {
            return (null, name, arity);
        }

        return (name[..lastDot], name[(lastDot + 1)..], arity);
    }

    private static int CountTopLevelCommas(string s)
    {
        int depth = 0, count = 0;
        foreach (var c in s)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    private Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation> ResolveSources(
        TraceLog traceLog,
        KeyValuePair<string, long>[] ranked,
        Dictionary<string, string> modules,
        Dictionary<string, Microsoft.Diagnostics.Tracing.Etlx.TraceCodeAddress> codeAddressByKey,
        SourceResolutionOptions options)
    {
        var result = new Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation>();
        var max = Math.Min(options.MaxResolved, ranked.Length);
        if (max <= 0) return result;

        Microsoft.Diagnostics.Symbols.SymbolReader? reader = null;
        try
        {
            // Derive a default symbol path from module directories so PDBs side-by-side
            // (the common case for managed apps published with portable PDBs) are found
            // even when NT_SYMBOL_PATH / _NT_SYMBOL_PATH is unset.
            var path = options.SymbolPath;
            if (string.IsNullOrEmpty(path))
            {
                var dirs = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < Math.Min(options.MaxResolved, ranked.Length); i++)
                {
                    if (!codeAddressByKey.TryGetValue(ranked[i].Key, out var addr)) continue;
                    var fp = addr.ModuleFile?.FilePath;
                    if (string.IsNullOrEmpty(fp)) continue;
                    var dir = Path.GetDirectoryName(fp);
                    if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
                }
                if (dirs.Count > 0) path = string.Join(Path.PathSeparator, dirs);
            }
            reader = new Microsoft.Diagnostics.Symbols.SymbolReader(
                Environment.GetEnvironmentVariable("DIAGMCP_SYMBOL_TRACE") == "1" ? Console.Out : TextWriter.Null,
                path);
            // SymbolReader treats PDBs sitting next to a managed assembly as "unsafe" by
            // default (legacy Windows convention). For the .NET portable-PDB case that's
            // exactly the location we want to honour — the sidecar reads files in a process
            // it already inspects, so accepting adjacent PDBs is the right trust boundary.
            reader.SecurityCheck = _ => true;
            // Pre-fetch PDBs once per module to avoid per-frame work.
            var loadedModules = new HashSet<Microsoft.Diagnostics.Tracing.Etlx.TraceModuleFile>();
            for (var i = 0; i < max; i++)
            {
                var key = ranked[i].Key;
                if (!codeAddressByKey.TryGetValue(key, out var addr)) continue;
                if (addr.ModuleFile is { } mf && loadedModules.Add(mf))
                {
                    try { traceLog.CodeAddresses.LookupSymbolsForModule(reader, mf); }
                    catch (Exception ex) { _logger.LogDebug(ex, "LookupSymbolsForModule failed for {Module}", mf.Name); }
                }
            }

            for (var i = 0; i < max; i++)
            {
                var key = ranked[i].Key;
                if (!codeAddressByKey.TryGetValue(key, out var addr)) continue;

                try
                {
                    var loc = addr.GetSourceLine(reader);
                    if (loc is null) continue;
                    var module = modules.GetValueOrDefault(key, string.Empty);
                    var methodDisplay = !string.IsNullOrEmpty(module) && key.StartsWith(module + "!", StringComparison.Ordinal)
                        ? key[(module.Length + 1)..]
                        : key;
                    var symbol = new DotnetDiagnosticsMcp.Core.Memory.SymbolRef(module, methodDisplay);
                    var file = loc.SourceFile?.BuildTimeFilePath;
                    var url = loc.SourceFile?.Url;
                    int? line = loc.LineNumber > 0 ? loc.LineNumber : null;
                    if (file is null && url is null && line is null) continue;
                    result[symbol] = new DotnetDiagnosticsMcp.Core.Memory.SourceLocation(file, line, url);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetSourceLine failed for {Method}", key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Source resolution skipped (symbol reader init failed).");
        }
        finally
        {
            reader?.Dispose();
        }
        return result;
    }

    private static CallTreeNode EmptyRoot() => new(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());

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
