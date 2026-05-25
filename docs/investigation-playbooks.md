# Investigation playbooks

Concrete, tool-by-tool recipes for the most common diagnostics scenarios an
LLM (or a human) can drive through `dotnet-diagnostics-mcp`. Each playbook starts from
a symptom and walks through the tool calls in order.

> **Always start with these two calls:**
>
> 1. `inspect_process(view="list")` — discover the target's PID
> 2. `inspect_process(view="capabilities")` — confirm runtime flavor (CoreCLR vs
>    NativeAOT) and which tools are usable
>
> The capability matrix gates the rest of the investigation. Skipping it leads
> to "CPU sampling returned nothing" surprises on NativeAOT targets.

---

## 1. "The app feels slow / high latency"

**Hypothesis tree:** CPU bound → GC bound → I/O / downstream bound → contention.

### Step 1 — Quick vitals
Call `collect_events(kind="counters")` with default providers for 5 s. If the target emits
Meter data, prefer `http.server.request.duration` p95 from `Meters[]`; otherwise fall back to
legacy EventCounters. Look at:

- `System.Runtime/cpu-usage`
- `System.Runtime/working-set`
- `System.Runtime/gen-2-gc-count` and `time-in-gc`
- `Meters[].Instrument == "http.server.request.duration"` (`Histogram.P95`), or `Microsoft.AspNetCore.Hosting/request-duration` if the Meter is absent
- `Microsoft-AspNetCore-Server-Kestrel/connection-queue-length`

### Step 2 — Branch on what's elevated

- **CPU near 100% in one or two cores** → `collect_sample(kind="cpu")` for 10–30 s,
  inspect `topHotspots` by `exclusiveSamples`. Look for unexpected user code
  near the top; hot framework methods often point to allocation pressure
  rather than algorithmic cost.
- **`time-in-gc` > 20% or rising gen-2 count** → `collect_events(kind="gc")` for
  10 s, look at `maxPauseTime` and the generation distribution. Gen-2 spikes
  with `WithHeap` dumps are the next step.
- **High request duration but low CPU** → `collect_events(kind="event_source")` with
  `providerName = "System.Net.Http"` to see outbound call timing, or
  `Microsoft.AspNetCore.Hosting` for in-pipeline latency. Often the answer is
  a downstream dependency, not the app itself.
- **Connection queue growing** → thread-pool starvation. `collect_events(kind="event_source")`
  with `Microsoft-System-Threading` (or `Microsoft-System-Threading-Tasks-TplEventSource`)
  shows queued/completed tasks per second.

---

## 2. "Memory keeps growing"

### Step 1
`collect_events(kind="counters")` for 15 s. Compare:

- `System.Runtime/working-set`
- `System.Runtime/gc-heap-size`
- `System.Runtime/gen-2-size`
- `System.Runtime/loh-size`
- `System.Runtime/poh-size`

A steadily-growing `gen-2-size` with a flat `working-set` is leak-shaped; both
growing is more like fragmentation or unmanaged growth.

### Step 2
`collect_events(kind="gc")` for 15–30 s. If gen-2 collections happen but `gen-2-size`
doesn't drop, you have surviving objects (leak or long-lived cache).

### Step 3
`collect_process_dump` with `dumpType = "WithHeap"`. **Defense in depth (B5.6 / RFC
0001 §4):** call it once first *without* `confirm` to preview the dump that would be
written (returns a `{ kind: "confirmation_required", targetPid, dumpType,
outputDirectory }` envelope and writes nothing); then re-issue with `confirm=true`
once a human has approved. The `dump-write` + `ptrace` scopes are still required on
top of `confirm=true`. Analyze offline:

```bash
dotnet dump analyze /tmp/dotnet-diagnostics-mcp/dump_pid12345_WithHeap_*.dmp
> dumpheap -stat
> gcroot <addr>
```

For a sidecar deployment, copy the dump out of the container first
(`kubectl cp`), since dotnet-dump usually runs alongside symbol/source paths
that aren't in the sidecar image.

---

## 3. "We're seeing 5xxs in production"

### Step 1
`collect_events(kind="exceptions")` for 30 s. Inspect `byType` to find the dominant exception
type, then look at `recent` to read messages and HRESULTs.

### Step 2
For first-chance vs unhandled differentiation, also call `collect_events(kind="event_source")`
with `providerName = "Microsoft-Extensions-Logging"`. This catches structured
log entries the app considers "handled" so you can correlate.

### Step 3 (optional)
If the exception only repros under specific code paths, capture a Mini dump at
the moment of an alert with `collect_process_dump dumpType=Mini` to inspect
thread stacks and locals.

---

## 4. "Slow outbound HTTP calls"

### Step 1
`collect_events(kind="event_source")` with `providerName = "System.Net.Http"`,
`durationSeconds = 30`. Look at `events` for `RequestStart` / `RequestStop`
pairs — most clients emit timing on the stop event payload.

### Step 2
Cross-reference with `Microsoft-AspNetCore-Server-Kestrel` for inbound
connection lifecycle to confirm the latency is downstream-induced, not
client-induced.

---

## 5. "Is this a NativeAOT app?"

`inspect_process(view="capabilities")` returns `runtime: "NativeAot"`. On NativeAOT:

- ✅ counters, exceptions, GC events, custom EventSources, dumps all work
- ❌ `collect_sample(kind="cpu")` returns no hotspots on EventPipe (SampleProfiler is CoreCLR-only); the NativeAOT Linux fallback routes through `perf record`
- ⚠️ EventSource support is opt-in via `<EventSourceSupport>true</EventSourceSupport>`
  on the target; if disabled at publish time even counters won't flow

For CPU sampling on NativeAOT, fall back to `perf record -p <pid>` on the
host. We may add a `perf`-based collector in the future (see plan, Phase 7).

---

## 6. "I'm investigating in production — what's the safest thing I can do?"

Order of escalation from cheapest to most disruptive:

1. `inspect_process(view="info")`, `inspect_process(view="capabilities")` — passive metadata
2. `collect_events(kind="counters")` — small EventPipe session, low overhead
3. `collect_events(kind="event_source")` / `collect_events(kind="exceptions")` / `collect_events(kind="gc")` — EventPipe sessions sized by `durationSeconds`
4. `collect_sample(kind="cpu")` — same family as 3 but specifically uses the SampleProfiler at ~1 kHz
5. `collect_process_dump dumpType=Mini` — pauses the process briefly while the kernel reads memory
6. `collect_process_dump dumpType=WithHeap` or `Full` — can pause the process for seconds and writes hundreds of MB to disk

In hot path scenarios, prefer windowed EventPipe tools (1–5) over dumps. Dumps
should be reserved for post-mortem or "we've already isolated this to one
instance" investigations.

---

## Adapting playbooks for the LLM

When wiring `dotnet-diagnostics-mcp` into an LLM-driven agent, encode this priority as
a system message:

> Always call `inspect_process(view="capabilities")` before any window-bound tool.
> Prefer `collect_events(kind="counters")` as the first observation; only escalate to CPU
> sampling, GC events, or dumps when the counters point in that direction.
> Never call `collect_process_dump` with `dumpType=Full` without explicit
> human approval.
