using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// ClrMD-backed implementation of <see cref="IDumpInspector"/>. Walks the managed heap
/// of a <c>.dmp</c> file produced by <see cref="IProcessDumper"/> (or any compatible
/// dump source) and reports aggregated statistics + the <see cref="TypeIdentity"/>
/// handoff payload for <c>dotnet-assembly-mcp</c>.
/// </summary>
/// <remarks>
/// Inspection is metadata-only and read-only: the dump file is opened with
/// <c>DataTarget.LoadDump</c> and never mutated. MVIDs are read directly from the PE on
/// disk via <see cref="System.Reflection.Metadata.MetadataReader"/> rather than from
/// ClrMD's module signature, which is sometimes a PDB signature rather than the MVID.
/// </remarks>
public sealed class ClrMdDumpInspector : IDumpInspector
{
    private readonly ILogger<ClrMdDumpInspector> _logger;

    public ClrMdDumpInspector(ILogger<ClrMdDumpInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<ClrMdDumpInspector>.Instance;
    }

    public Task<HeapSnapshotArtifact> InspectAsync(
        string dumpFilePath,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpFilePath);
        if (!File.Exists(dumpFilePath))
        {
            throw new FileNotFoundException("Dump file not found.", dumpFilePath);
        }

        var opts = options ?? new DumpInspectionOptions();
        ValidateOptions(opts);

        // ClrMD is fully synchronous; wrap in Task.Run so the caller's async context isn't
        // blocked on a multi-second heap walk for a large dump.
        return Task.Run(() => Inspect(dumpFilePath, opts, cancellationToken), cancellationToken);
    }

    public Task<HeapSnapshotArtifact> InspectLiveAsync(
        int processId,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        var opts = options ?? new DumpInspectionOptions();
        ValidateOptions(opts);

        return Task.Run(() => InspectLive(processId, opts, cancellationToken), cancellationToken);
    }

    private static void ValidateOptions(DumpInspectionOptions opts)
    {
        if (opts.TopTypes <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "TopTypes must be positive.");
        if (opts.SnapshotTopTypes <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "SnapshotTopTypes must be positive.");
        if (opts.RetentionPathLimit <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "RetentionPathLimit must be positive.");
        if (opts.SnapshotRetentionPathTargets <= 0) throw new ArgumentOutOfRangeException(nameof(opts), "SnapshotRetentionPathTargets must be positive.");
    }

    private HeapSnapshotArtifact InspectLive(int processId, DumpInspectionOptions opts, CancellationToken ct)
    {
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // suspend=true pauses the target for the lifetime of the DataTarget. We dispose
        // ASAP after the walk so the suspend window is bounded by walk duration.
        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime (NativeAOT or non-managed).");
        using var runtime = clrInfo.CreateRuntime();

        var runtimeInfo = BuildRuntimeInfo(target, clrInfo, runtime);

        if (!runtime.Heap.CanWalkHeap)
        {
            warnings.Add("Heap walk is unavailable for this runtime state (CanWalkHeap=false). Retry once the GC is in a quiescent state.");
            sw.Stop();
            return new HeapSnapshotArtifact(
                Origin: HeapSnapshotOrigin.Live,
                ProcessId: processId,
                CapturedAt: capturedAt,
                WalkDuration: sw.Elapsed,
                Runtime: runtimeInfo,
                Heap: EmptyHeap(),
                TopTypesByBytes: Array.Empty<TypeStat>(),
                TopTypesByInstances: Array.Empty<TypeStat>())
            {
                Warnings = warnings,
            };
        }

        var (byBytes, byInstances, heapSummary, retention, roots, finalizable, segments) = SummarizeRuntime(runtime, opts, warnings, ct);
        sw.Stop();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            Runtime: runtimeInfo,
            Heap: heapSummary,
            TopTypesByBytes: byBytes,
            TopTypesByInstances: byInstances)
        {
            RetentionPaths = retention,
            RootsByKind = roots,
            FinalizableObjectsByType = finalizable,
            Segments = segments,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static DumpRuntimeInfo BuildRuntimeInfo(DataTarget target, ClrInfo clrInfo, ClrRuntime runtime) =>
        new(
            Name: clrInfo.Flavor.ToString(),
            Version: clrInfo.Version.ToString(),
            Architecture: target.DataReader.Architecture.ToString(),
            IsServerGC: runtime.Heap.IsServer,
            HeapCount: runtime.Heap.SubHeaps.Length);

    private HeapSnapshotArtifact Inspect(string dumpFilePath, DumpInspectionOptions opts, CancellationToken ct)
    {
        var fileInfo = new FileInfo(dumpFilePath);
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var target = DataTarget.LoadDump(dumpFilePath);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("Dump does not contain a CLR runtime.");
        using var runtime = clrInfo.CreateRuntime();

        var runtimeInfo = BuildRuntimeInfo(target, clrInfo, runtime);
        var processIdFromDump = unchecked((int)target.DataReader.ProcessId);

        if (!runtime.Heap.CanWalkHeap)
        {
            warnings.Add("Heap walk is unavailable for this dump (CanWalkHeap=false). " +
                "Capture a WithHeap or Full dump for full inspection.");
            sw.Stop();
            return new HeapSnapshotArtifact(
                Origin: HeapSnapshotOrigin.Dump,
                ProcessId: processIdFromDump,
                CapturedAt: capturedAt,
                WalkDuration: sw.Elapsed,
                Runtime: runtimeInfo,
                Heap: EmptyHeap(),
                TopTypesByBytes: Array.Empty<TypeStat>(),
                TopTypesByInstances: Array.Empty<TypeStat>())
            {
                DumpFilePath = dumpFilePath,
                DumpFileSizeBytes = fileInfo.Length,
                Warnings = warnings,
            };
        }

        var (byBytes, byInstances, heapSummary, retention, roots, finalizable, segments) = SummarizeRuntime(runtime, opts, warnings, ct);
        sw.Stop();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Dump,
            ProcessId: processIdFromDump,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            Runtime: runtimeInfo,
            Heap: heapSummary,
            TopTypesByBytes: byBytes,
            TopTypesByInstances: byInstances)
        {
            DumpFilePath = dumpFilePath,
            DumpFileSizeBytes = fileInfo.Length,
            RetentionPaths = retention,
            RootsByKind = roots,
            FinalizableObjectsByType = finalizable,
            Segments = segments,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private (IReadOnlyList<TypeStat> ByBytes, IReadOnlyList<TypeStat> ByInstances, DumpHeapSummary Heap, IReadOnlyList<RetentionPath>? Retention, IReadOnlyList<RootKindStat> Roots, IReadOnlyList<FinalizableTypeStat> Finalizable, IReadOnlyList<SegmentStat> Segments) SummarizeRuntime(
        ClrRuntime runtime, DumpInspectionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var (typeStats, totalBytes, segments) = WalkHeap(runtime, ct);
        var heapSummary = SummarizeHeapFromSegments(segments);

        // The snapshot retains a richer top-N so follow-up drilldown queries (e.g. ask for top-100
        // when the tool returned top-20 inline) don't pay the walk cost a second time.
        var snapshotTopN = Math.Max(opts.TopTypes, opts.SnapshotTopTypes);

        var byBytes = typeStats.Values
            .OrderByDescending(s => s.Bytes)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        var byInstances = typeStats.Values
            .OrderByDescending(s => s.Count)
            .Take(snapshotTopN)
            .Select(s => ToTypeStat(s, totalBytes))
            .ToArray();

        IReadOnlyList<RetentionPath>? retention = null;
        if (opts.IncludeRetentionPaths)
        {
            retention = ResolveRetentionPaths(runtime, byBytes, opts.RetentionPathLimit, opts.SnapshotRetentionPathTargets, warnings, ct);
        }

        var roots = WalkRoots(runtime, warnings, ct);
        var finalizable = WalkFinalizableObjects(runtime, opts.SnapshotFinalizerQueueTopTypes, warnings, ct);

        return (byBytes, byInstances, heapSummary, retention, roots, finalizable, segments);
    }

    private static (Dictionary<TypeKey, RawTypeStat> Stats, long TotalBytes, IReadOnlyList<SegmentStat> Segments) WalkHeap(ClrRuntime runtime, CancellationToken ct)
    {
        var stats = new Dictionary<TypeKey, RawTypeStat>();
        long total = 0;
        var segmentStats = new List<SegmentStat>(runtime.Heap.Segments.Length);

        foreach (var segment in runtime.Heap.Segments)
        {
            ct.ThrowIfCancellationRequested();
            long segUsed = 0, segFree = 0, segObjs = 0, segFreeObjs = 0;

            foreach (var obj in segment.EnumerateObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (obj.Type is null) continue;
                var size = (long)obj.Size;
                segObjs++;

                if (obj.IsFree)
                {
                    segFree += size;
                    segFreeObjs++;
                    continue;
                }

                segUsed += size;
                total += size;

                var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
                if (!stats.TryGetValue(key, out var s))
                {
                    s = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                    stats[key] = s;
                }
                s.Count++;
                s.Bytes += size;
            }

            var length = (long)segment.Length;
            var committed = (long)(segment.CommittedMemory.End - segment.CommittedMemory.Start);
            var reserved = (long)(segment.ReservedMemory.End - segment.ReservedMemory.Start);
            var freePct = length > 0 ? Math.Round(100.0 * segFree / length, 2) : 0.0;

            segmentStats.Add(new SegmentStat(
                LogicalHeap: segment.SubHeap.Index,
                Kind: segment.Kind.ToString(),
                Generation: ClassifySegmentGeneration(segment),
                Start: segment.Start,
                End: segment.End,
                Length: length,
                CommittedBytes: committed,
                ReservedBytes: reserved,
                UsedBytes: segUsed,
                FreeBytes: segFree,
                ObjectCount: segObjs - segFreeObjs,
                FreeObjectCount: segFreeObjs)
            {
                FreePercent = freePct,
            });
        }
        return (stats, total, segmentStats);
    }

    private static string ClassifySegmentGeneration(ClrSegment segment) => segment.Kind switch
    {
        GCSegmentKind.Generation0 => "Gen0",
        GCSegmentKind.Generation1 => "Gen1",
        GCSegmentKind.Generation2 => "Gen2",
        GCSegmentKind.Large => "LOH",
        GCSegmentKind.Pinned => "POH",
        GCSegmentKind.Ephemeral => "Ephemeral",
        GCSegmentKind.Frozen => "Frozen",
        _ => segment.Kind.ToString(),
    };

    private static RootKindStat[] WalkRoots(ClrRuntime runtime, List<string> warnings, CancellationToken ct)
    {
        // Bucket every reachable root by ClrRootKind. We deliberately do NOT do a per-root
        // retention walk here (that's O(roots × heap) and would dwarf the heap walk itself).
        // DirectlyReferencedBytes is the sum of the IMMEDIATE target object's size, summed across
        // distinct objects per kind. Useful for spotting "I have 50k pinning handles holding X MB".
        var byKind = new Dictionary<string, RawRootStat>(StringComparer.Ordinal);

        try
        {
            foreach (var root in runtime.Heap.EnumerateRoots())
            {
                ct.ThrowIfCancellationRequested();
                var kind = root.RootKind.ToString();
                if (!byKind.TryGetValue(kind, out var bucket))
                {
                    bucket = new RawRootStat();
                    byKind[kind] = bucket;
                }
                bucket.RootCount++;
                if (root.IsPinned) bucket.PinnedCount++;
                if (root.IsInterior) bucket.InteriorCount++;

                var addr = root.Object.Address;
                if (addr != 0 && bucket.SeenObjects.Add(addr))
                {
                    bucket.DistinctTargets++;
                    if (!root.Object.IsNull && root.Object.Type is not null)
                    {
                        bucket.DirectBytes += (long)root.Object.Size;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Root enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        return byKind
            .Select(kvp => new RootKindStat(
                RootKind: kvp.Key,
                RootCount: kvp.Value.RootCount,
                DistinctTargetObjects: kvp.Value.DistinctTargets,
                DirectlyReferencedBytes: kvp.Value.DirectBytes,
                PinnedRootCount: kvp.Value.PinnedCount,
                InteriorRootCount: kvp.Value.InteriorCount))
            .OrderByDescending(r => r.DirectlyReferencedBytes)
            .ThenByDescending(r => r.RootCount)
            .ToArray();
    }

    private static FinalizableTypeStat[] WalkFinalizableObjects(
        ClrRuntime runtime, int topN, List<string> warnings, CancellationToken ct)
    {
        var byType = new Dictionary<TypeKey, RawTypeStat>();
        try
        {
            foreach (var addr in runtime.Heap.EnumerateFinalizableObjects())
            {
                ct.ThrowIfCancellationRequested();
                var obj = runtime.Heap.GetObject(addr);
                if (obj.Type is null) continue;
                var key = new TypeKey(obj.Type.Name ?? "<unknown>", obj.Type.Module?.Name);
                if (!byType.TryGetValue(key, out var bucket))
                {
                    bucket = new RawTypeStat(key.TypeName, key.ModuleName, obj.Type);
                    byType[key] = bucket;
                }
                bucket.Count++;
                bucket.Bytes += (long)obj.Size;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Finalizer queue enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        return byType.Values
            .OrderByDescending(b => b.Bytes)
            .ThenByDescending(b => b.Count)
            .Take(topN)
            .Select(b => new FinalizableTypeStat(
                TypeFullName: b.TypeName,
                ModuleName: b.ModuleName is { } mn ? Path.GetFileName(mn) : null,
                InstanceCount: b.Count,
                TotalBytes: b.Bytes))
            .ToArray();
    }

    private TypeStat ToTypeStat(RawTypeStat raw, long totalBytes)
    {
        var pct = totalBytes > 0 ? Math.Round(100.0 * raw.Bytes / totalBytes, 2) : 0.0;
        return new TypeStat(
            TypeFullName: raw.TypeName,
            ModuleName: raw.ModuleName is { } mn ? Path.GetFileName(mn) : null,
            InstanceCount: raw.Count,
            TotalBytes: raw.Bytes,
            TotalBytesPercent: pct,
            Identity: BuildTypeIdentity(raw));
    }

    private TypeIdentity? BuildTypeIdentity(RawTypeStat raw)
    {
        var clrType = raw.ClrType;
        if (clrType is null) return null;

        var modulePath = clrType.Module?.Name;
        var moduleFileName = !string.IsNullOrEmpty(modulePath) ? Path.GetFileName(modulePath) : null;
        var token = (int)clrType.MetadataToken;
        var mvid = TryReadMvid(modulePath);

        if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleFileName))
        {
            return null;
        }

        return new TypeIdentity(raw.TypeName)
        {
            ModuleName = moduleFileName,
            ModulePath = modulePath,
            ModuleVersionId = mvid,
            MetadataToken = token > 0 ? token : null,
        };
    }

    private Guid? TryReadMvid(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        if (!File.Exists(assemblyPath)) return null;
        if (_mvidCache.TryGetValue(assemblyPath, out var cached)) return cached;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                _mvidCache[assemblyPath] = null;
                return null;
            }
            var metadata = peReader.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            _mvidCache[assemblyPath] = mvid;
            return mvid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MVID read failed for {Path}", assemblyPath);
            _mvidCache[assemblyPath] = null;
            return null;
        }
    }

    private readonly Dictionary<string, Guid?> _mvidCache = new(StringComparer.Ordinal);

    private static DumpHeapSummary SummarizeHeapFromSegments(IReadOnlyList<SegmentStat> segments)
    {
        long total = 0, gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poh = 0, committed = 0;
        foreach (var s in segments)
        {
            total += s.Length;
            committed += s.CommittedBytes;
            switch (s.Generation)
            {
                case "Gen0": gen0 += s.Length; break;
                case "Gen1": gen1 += s.Length; break;
                case "Gen2": gen2 += s.Length; break;
                case "LOH": loh += s.Length; break;
                case "POH": poh += s.Length; break;
                case "Ephemeral":
                    // Workstation GC keeps gen0/gen1 in a single segment; bucket into gen0 for the LLM-facing summary.
                    gen0 += s.Length;
                    break;
            }
        }
        return new DumpHeapSummary(total, gen0, gen1, gen2, loh, poh, committed);
    }

    private static DumpHeapSummary EmptyHeap() => new(0, 0, 0, 0, 0, 0, 0);

    private static IReadOnlyList<RetentionPath> ResolveRetentionPaths(
        ClrRuntime runtime,
        IReadOnlyList<TypeStat> topByBytes,
        int depthLimit,
        int targetCount,
        List<string> warnings,
        CancellationToken ct)
    {
        // Build a reverse map: object → first retainer found during a single roots/refs walk.
        // For each target type we then pick the largest instance and walk back to a root.
        // This is approximate (a real !gcroot does a full search) but cheap and "good enough"
        // to point the LLM at where to dig deeper.
        var targets = new HashSet<string>(topByBytes.Take(targetCount).Select(t => t.TypeFullName), StringComparer.Ordinal);
        if (targets.Count == 0) return Array.Empty<RetentionPath>();

        var sampleInstances = new Dictionary<string, ClrObject>(StringComparer.Ordinal);
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            var typeName = obj.Type?.Name;
            if (typeName is null || !targets.Contains(typeName)) continue;
            if (sampleInstances.TryGetValue(typeName, out var existing) && existing.Size >= obj.Size) continue;
            sampleInstances[typeName] = obj;
        }

        var rootByObject = BuildRootByObjectMap(runtime, sampleInstances.Values, depthLimit, ct);

        var results = new List<RetentionPath>(sampleInstances.Count);
        foreach (var (typeName, instance) in sampleInstances)
        {
            ct.ThrowIfCancellationRequested();
            var chain = WalkUp(instance, rootByObject, depthLimit, out var truncated);
            results.Add(new RetentionPath(
                TargetTypeFullName: typeName,
                TargetObjectAddress: instance.Address,
                Chain: chain,
                Truncated: truncated));
        }
        return results;
    }

    private static Dictionary<ulong, (ulong From, string? RootKind)> BuildRootByObjectMap(
        ClrRuntime runtime,
        IEnumerable<ClrObject> _,
        int depthLimit,
        CancellationToken ct)
    {
        // Map each reachable object to its first-seen retainer (object address or root).
        var retainer = new Dictionary<ulong, (ulong From, string? RootKind)>();
        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong Address, int Depth)>();

        foreach (var root in runtime.Heap.EnumerateRoots())
        {
            ct.ThrowIfCancellationRequested();
            var addr = root.Object.Address;
            if (addr == 0 || visited.Contains(addr)) continue;
            visited.Add(addr);
            retainer[addr] = (0UL, root.RootKind.ToString());
            queue.Enqueue((addr, 0));
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (addr, depth) = queue.Dequeue();
            if (depth >= depthLimit * 8) continue; // safety cap on the BFS itself
            ClrObject obj;
            try { obj = runtime.Heap.GetObject(addr); }
            catch { continue; }
            if (obj.Type is null) continue;

            foreach (var child in obj.EnumerateReferences())
            {
                if (child.Address == 0 || !visited.Add(child.Address)) continue;
                retainer[child.Address] = (addr, null);
                queue.Enqueue((child.Address, depth + 1));
            }
        }
        return retainer;
    }

    private static List<RetentionFrame> WalkUp(
        ClrObject instance,
        Dictionary<ulong, (ulong From, string? RootKind)> retainerMap,
        int depthLimit,
        out bool truncated)
    {
        var chain = new List<RetentionFrame>(depthLimit + 1);
        var current = instance.Address;
        var visited = new HashSet<ulong> { current };
        truncated = false;

        chain.Add(new RetentionFrame(instance.Type?.Name ?? "<unknown>", current));

        for (var i = 0; i < depthLimit; i++)
        {
            if (!retainerMap.TryGetValue(current, out var step)) break;
            if (step.From == 0)
            {
                chain.Add(new RetentionFrame("<root>", 0) { RootKind = step.RootKind ?? "Unknown" });
                return chain;
            }
            if (!visited.Add(step.From)) break;
            // We don't have the ClrObject in hand here; just record the address. Resolving
            // the type name requires another GetObject which we skip for cost — agent can
            // call back into the dump for the specific address if needed.
            chain.Add(new RetentionFrame("<retainer>", step.From));
            current = step.From;
        }

        truncated = chain.Count > depthLimit;
        return chain;
    }

    private readonly record struct TypeKey(string TypeName, string? ModuleName);

    private sealed class RawRootStat
    {
        public long RootCount;
        public long DistinctTargets;
        public long DirectBytes;
        public long PinnedCount;
        public long InteriorCount;
        public HashSet<ulong> SeenObjects { get; } = new();
    }

    private sealed class RawTypeStat
    {
        public RawTypeStat(string typeName, string? moduleName, ClrType clrType)
        {
            TypeName = typeName;
            ModuleName = moduleName;
            ClrType = clrType;
        }
        public string TypeName { get; }
        public string? ModuleName { get; }
        public ClrType ClrType { get; }
        public long Count;
        public long Bytes;
    }
}
