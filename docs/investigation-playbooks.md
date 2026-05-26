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

### Step 2 — Quick app-level signal
`collect_events(kind="logs", minLevel="Warning")` for 10–15 s. If the warning/error stream already names the slow dependency, timeout, or retry loop, follow that lead before escalating.

### Step 3 — Branch on what's elevated

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
- **Connection queue growing** → thread-pool starvation. `collect_events(kind="threadpool")`
  for 6–10 s, then inspect `query_snapshot(handle, view="timeline")` for worker/IOCP growth,
  `view="hillClimbing"` for `Starvation` / `ThreadTimedOut` transitions, and
  `view="workItemOrigins"` for the hottest enqueue origins when call stacks are available.

---

## 1a. "ThreadPool starvation / sync-over-async"

1. Run `collect_events(kind="threadpool", durationSeconds=6)` **before** the suspected blocking workload starts.
2. Drive the workload (for example `GET /threadpool-starve?blockers=50` in `BadCodeSample`) while the window is open.
3. Read the inline summary first:
   - `starvationAdjustments > 0` or `hillClimbingEvents > 0` + rising worker timeline → starvation confirmed.
   - `effectiveSettings` near `workerMinThreads` with a flat worker timeline → the pool may not be injecting quickly enough.
4. Drill down with `query_snapshot`:
   - `view="timeline"` → worker vs IOCP bucketed counts.
   - `view="hillClimbing"` → exact transition sequence (`Warmup`, `Starvation`, `ThreadTimedOut`, …).
   - `view="workItemOrigins"` → hottest enqueue origins when EventPipe call stacks are available.
5. If the pool keeps growing but the app stays slow, pair the result with `collect_sample(kind="cpu")` or `collect_thread_snapshot(view="threadpool")` to identify the blocking code.

## 1a.1. "Lock contention / monitor storm"

1. Start `collect_events(kind="contention", durationSeconds=6)` **before** driving the workload.
2. Hit `GET /lock-storm?seconds=3&blockers=8` in `BadCodeSample` while the window is open.
3. Read `summary` first:
   - `totalEvents > 0` + high `p95ContentionDuration` → lock waits are a likely latency root cause.
   - `distinctMonitors == 1` → one hot gate is serializing the path.
4. Drill with `query_snapshot(handle, view="byCallSite")` to find the hottest contended method, then `view="byOwner"` to see which owner thread is repeatedly holding the monitor.
5. If contention is severe but the call site remains framework-heavy, pair it with `collect_thread_snapshot(view="lock-graph")` while the incident is live.

- **Endpoint-specific latency with DB suspicion** → `collect_events(kind="db")`
  for 10–15 s while driving the slow request. Check `summary` / `byCommand` for
  hot SQL shapes, `n+1` for repeated command bursts under one parent activity,
  and `connectionPool` for open-connection pressure or exhaustion signals.
- **Connection queue growing** → thread-pool starvation. `collect_events(kind="event_source")`
  with `Microsoft-System-Threading` (or `Microsoft-System-Threading-Tasks-TplEventSource`)
  shows queued/completed tasks per second.

---

## 1b. "Did this deploy regress CPU or allocation hot spots?"

1. Capture a baseline window on the healthy / previous deploy: `collect_sample(kind="cpu")`
   or `collect_sample(kind="allocation")`.
2. Capture the same window on the current deploy.
3. Diff them with `query_snapshot(handle="<current>", view="diff", baselineHandle="<baseline>")`.
4. Look at `Changed[]` first:
   - `Direction="up"` + `Verdict="regression"|"mixed"` → hot path / type got worse.
   - `Direction="down"` → improvement.
   - `Notes[]` mentioning normalization → allocation windows had different durations; use the
     per-second metrics instead of raw totals.
5. If the diff points at a CPU hotspot, follow up with `query_snapshot(view="call-tree")` on the
   current handle to walk callers/callees for the regressed method.

---

## 1c. "Post-deploy cold-start is slow"

1. Start `collect_events(kind="jit", durationSeconds=10)` **before** sending the first real request after deploy / rollout.
2. During the window, hit the cold path once or a small handful of times.
3. Inspect `summary` first:
   - high `distribution.tier0` with low `tier1Percent` → the process is still mostly running first-pass codegen
   - low `r2rHitRatePercent` or high `distribution.r2rMissThenJit` → ReadyToRun coverage is poor for this startup path
   - non-zero `reJitCount` / `osrCount` → tiered recompilation is already happening during warmup
4. Drill in with `query_snapshot(handle, view="topMethods")` to see which methods paid the largest inclusive JIT cost.
5. If the same endpoints stay slow after the cold window, pivot to `collect_sample(kind="cpu")` — the problem is no longer just startup compilation.

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
If `working-set` / RSS is growing **without** corresponding `gc-heap-size` growth,
branch to `inspect_process(view="resources")` before taking a dump. This catches the
classic unmanaged-FD leak shape:

- rising `fdCount` + `noFileUsageFraction` → file/socket leak approaching `ulimit -n`
- rising `sockets.closeWait` → likely undisposed `HttpResponseMessage` / pooled HTTP misuse
- huge `sockets.timeWait` with flat `fdCount` → connection churn / pooling misconfiguration

If `resources` looks clean, continue with GC-focused investigation.

### Step 3
`collect_events(kind="gc")` for 15–30 s. If gen-2 collections happen but `gen-2-size`
doesn't drop, you have surviving objects (leak or long-lived cache).

### Step 4
If `working-set` keeps climbing but the managed heap still looks deceptively small,
run `query_snapshot(handle, view="gchandles")` on a recent `inspect_heap` handle.
A growing `Pinned` / `Normal` bucket is the classic forgotten-`GCHandle.Alloc(...)`
shape; `Dependent` often points at `ConditionalWeakTable`-style leaks.

### Step 5
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

## 2b. "Is this Server GC / did someone override ThreadPool or tiered compilation?"

1. Call `inspect_process(view="runtime-config")` against the target PID.
2. Read `gc` first:
   - `isServerGc=true` + `heapCount > 1` → Server GC is active.
   - `isConcurrent=false` / `isBackground=false` → expect longer stop-the-world pauses than the default CoreCLR workstation profile.
3. Read `threadPool` next:
   - unexpectedly low `minWorkerThreads` or `hillClimbingEnabled=false` → keep starvation in mind before blaming downstream I/O.
4. Check `tieredCompilation` / `envVars`:
   - `DOTNET_TieredCompilation=0` / `DOTNET_TieredPGO=0` overrides explain surprising cold-start or steady-state perf behavior.
   - `notes[]` explicitly says when a field is unavailable (for example ptrace-gated ClrMD attach on Linux).

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

## 4b. "One endpoint is hanging right now"

1. Call `inspect_process(view="requests-now")` while the incident is happening. It opens a short (~2 s) request window and returns only in-flight ASP.NET Core requests, with the current thread id and top stack frames.
2. Sort mentally by `startedAtMs` — the oldest request is your best candidate.
3. Look at `topFrames[]`:
   - app code near the top → you already have the first suspect method
   - `Task.Delay`, timers, waits, or `Monitor.Enter` → likely async hang / lock contention
   - framework I/O (`Socket`, `SslStream`, `HttpClient`) → pivot to `collect_events(kind="event_source", providerName="System.Net.Http")`
4. If the single-thread view is not enough, escalate to `collect_thread_snapshot` for the full thread + lock graph while the same request is still hanging.
5. Reproduce locally with `samples/BadCodeSample`'s `/slow-hang?seconds=N` fixture.
## 4b. "Slow query / N+1 suspected"

1. Start `collect_events(kind="db", durationSeconds=10-15)` **before** driving the
   slow endpoint.
2. Hit the endpoint while the collection window is open.
3. Read `summary` first:
   - `topCommands[0].p95Ms` high → slow query shape
   - `nPlusOneCount > 0` → repeated SQL under one parent activity / trace
   - `connectionPool.poolExhaustedCount > 0` → pool starvation or leaked
     connections
4. Drill with `query_snapshot(handle, view="byCommand")` to inspect the worst SQL
   shapes, then `query_snapshot(handle, view="n+1")` to confirm the repeated
   pattern and how many times it fired.
5. If the DB snapshot is quiet, fall back to `collect_events(kind="event_source",
   providerName="System.Net.Http")` or `collect_sample(kind="cpu")` — the latency
   may be downstream or CPU-bound rather than database-bound.

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
