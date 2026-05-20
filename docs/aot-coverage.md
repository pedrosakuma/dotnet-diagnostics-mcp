# NativeAOT diagnostic coverage

This document maps the common diagnostic questions to the tools that can answer
them, distinguishing **CoreCLR** (JIT-based, all tools) from **NativeAOT**
(ahead-of-time compiled, no JIT metadata at runtime).

## Capability matrix

| Question | CoreCLR tool | NativeAOT tool | Notes |
|---|---|---|---|
| Is the process .NET? | `list_dotnet_processes` | `list_dotnet_processes` | Both expose a diagnostic IPC socket |
| What runtime is this? | `get_diagnostic_capabilities` | `get_diagnostic_capabilities` | Detects CoreCLR vs NativeAOT |
| Runtime counters (CPU, GC, heap, threads) | `snapshot_counters` | `snapshot_counters` | EventPipe works on both |
| GC pause frequency and duration | `collect_gc_events` | `collect_gc_events` | GC keyword on both runtimes |
| Exception rate and types | `collect_exceptions` | `collect_exceptions` | CLR exception events on both |
| Custom EventSource events | `collect_event_source` | `collect_event_source` | Provider must be embedded in AOT binary |
| **Who is allocating?** | **`collect_allocation_sample`** | **`collect_allocation_sample`** | GCAllocationTick — works on both |
| What types dominate the heap? | `inspect_live_heap`, `inspect_dump` | ❌ not available | ClrMD requires JIT metadata |
| What method is hot (CPU)? | `collect_cpu_sample` (EventPipe) | `collect_cpu_sample` (perf/ETW) | AOT: native frames only |
| Off-CPU blocking stacks | `collect_off_cpu_sample` | `collect_off_cpu_sample` | perf/ETW |
| Thread stacks | `collect_thread_snapshot` | ❌ not available | ClrMD requires JIT metadata |
| Process dump | `collect_process_dump` | `collect_process_dump` | Dump is native-only on AOT |
| Container throttling / cgroup | `get_container_signals` | `get_container_signals` | Reads `/sys/fs/cgroup`, not runtime-specific |

## AOT heap diagnostics — the allocation sampling answer

On CoreCLR, the typical flow for "memory keeps growing" is:

```
inspect_live_heap → query_heap_snapshot(view="topByBytes")
```

On NativeAOT, `inspect_live_heap` is unavailable because ClrMD cannot walk the
heap without JIT-emitted type metadata. The correct substitute is:

```
collect_allocation_sample(processId, durationSeconds=30)
```

`collect_allocation_sample` uses `GCAllocationTick` from
`Microsoft-Windows-DotNETRuntime` (GCKeyword, Verbose level). This event fires
every ~100 KB of managed allocations and carries the **TypeName** of the sampled
object. It is implemented in the shared GC used by both CoreCLR and NativeAOT.

### What you get

| Field | Description |
|---|---|
| `topByBytes` | Types ranked by total sampled bytes — the dominant pressure signal |
| `topByCount` | Types ranked by event count — useful for identifying bursty small-object allocators |
| `totalEvents` | Total `GCAllocationTick` events in the window |
| `totalBytes` | Sum of all sampled allocation amounts |
| `dominantKind` | Whether the type is mostly SOH (`Small`) or LOH/POH (`Large`) |

### What you don't get

- **Call stacks**: `GCAllocationTick` does not capture the call site. To find
  which code paths allocate a specific type, use `collect_cpu_sample` and look
  for hot paths that construct the type, or add a custom EventSource counter
  in the application.
- **Per-object sizes**: the event records `AllocationAmount64` (bytes since the
  last sample), not the size of a specific object.
- **Exact totals**: the 100 KB threshold means small single allocations may not
  be sampled at all. The ranking is statistically correct for steady workloads.

## Recommended AOT investigation playbook

```
1. get_diagnostic_capabilities   → confirm NativeAOT, note available tools
2. snapshot_counters             → baseline GC heap, gen counts, CPU
3. collect_allocation_sample(30) → identify top allocating types
4. collect_gc_events(30)         → correlate GC frequency with allocation load
5. collect_cpu_sample            → perf/ETW native frames (may need CAP_PERFMON)
```

If `gen-0-gc-count` is high in step 2 and `collect_allocation_sample` shows a
type allocating >100 MB/s, that type is the primary GC pressure source — reduce
allocations of that type or pool instances to improve throughput.
