# Tool reference

Every tool exposed by `dotnet-diagnostics-mcp` is listed here with its purpose, parameters,
return shape, runtime requirements, and a sample invocation. All tools are
delivered over Streamable HTTP at `POST /mcp` and require an
`Authorization: Bearer <token>` header (see [client-setup.md](./client-setup.md)).

> Return shapes link back to the C# record definitions in
> [`src/DotnetDiagnosticsMcp.Core`](../src/DotnetDiagnosticsMcp.Core), which are the source of
> truth for field names and types.

### Bootstrap implícito (`processId` is optional)

Since issue #42 every tool that targets a live .NET process accepts `processId`
as optional. When the caller omits it the server lists the visible .NET
processes via the diagnostic IPC and:

- **0 candidates** → structured error `NoDotnetProcessFound`.
- **1 candidate** → auto-selects it, marks the response's
  `resolvedProcess.autoResolved = true`.
- **N candidates** → structured error `AmbiguousDotnetProcess` with the
  candidate list inline; re-issue the call with `processId` set explicitly.

Every successful response now carries a `resolvedProcess` digest on the
envelope alongside `data` / `summary` / `hints`:

```json
{
  "resolvedProcess": {
    "processId": 1234,
    "runtime": "CoreClr",
    "runtimeVersion": "10.0.0",
    "canSampleCpu": true,
    "canCollectGcDump": true,
    "autoResolved": true
  }
}
```

This means the previously-obligatory opener of
`list_dotnet_processes` → `get_diagnostic_capabilities` → `<tool>` collapses to
a single `<tool>` call when there is only one .NET process visible to the
sidecar. The capability digest is cached per pid for 60 seconds so back-to-back
tool calls within an investigation pay the probe cost once.

### Verbosidade (`depth`)

Issue [#41 slice 2c](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/41)
adds a uniform `depth` parameter to every windowed collector. Values:
`Summary` (default), `Detail`, `Raw`. Contract:

- `Summary` returns a small, decision-grade payload inline (the smallest piece
  of evidence the LLM needs to choose the next tool). This is the default.
- `Detail` returns the historical pre-#41 payload (top-N hotspots, full
  `Events[]` lists, full `Notes`, etc.).
- `Raw` is reserved for parity with the artifact handle; today equivalent to
  `Detail` for every tool.

**Key invariant — the handle store always carries the FULL artifact**, regardless
of `depth`. The depth knob only filters the *inline* response. Drilldown tools
(`query_collection`, `query_off_cpu_snapshot`, `query_thread_snapshot`,
`get_call_tree`) keep returning everything the original collection captured.

Per-tool `Summary` semantics:

| Tool | What `Summary` drops inline |
| --- | --- |
| `snapshot_counters` | All non-headline counters (keeps ~12: cpu-usage, working-set, gc-heap-size, gen-2-gc-count, threadpool-thread-count, threadpool-queue-length, exception-count, monitor-lock-contention-count + ASP.NET Core requests/failed/current + Kestrel connections-per-sec). |
| `get_container_signals` | The `Notes[]` (caveats about cgroup v1 / missing PSI). Cgroup values themselves remain. |
| `collect_cpu_sample` | `TopHotspots` truncated to the top 3 (handle keeps `topN`, default 25). |
| `collect_off_cpu_sample` | `TopBlockingStacks` truncated to the top 3 (handle keeps `topN`). |
| `collect_exceptions` | The `Recent[]` list. `Total` and `ByType` remain exact (counts at every depth). |
| `collect_gc_events` | The `Events[]` list. Totals, max pause, per-gen counts remain exact. |
| `collect_event_source` | The `Events[]` list. Provider + total count remain. Drill in with `query_collection(handle, view=byEventName)`. |
| `collect_thread_snapshot` | The lock graph + threads beyond the top 3 most-blocked. Drill in with `query_thread_snapshot(view=lock-graph|deadlocks|unique-stacks)`. |

Explicit `topN` always wins over the depth default — if you pass
`topN=10, depth=Summary` you get up to 10 hotspots inline (the LLM knows what
it asked for).

`collect_activities` does **not** currently expose `depth`; it always returns the
retained `Activities[]` inline (bounded by `maxActivities`) and relies on
`query_collection(handle, view=...)` for narrower drilldown views.

### Long-running collects: MCP Tasks vs `runAsJob`

As of the `2025-11-25` protocol bump, the server registers an
`IMcpTaskStore`, advertises `capabilities.tasks.{list,cancel,requests.tools.call}`
and marks these tools with `execution.taskSupport: "optional"` in `tools/list`:

- `collect_cpu_sample`
- `collect_exceptions`
- `collect_gc_events`

**Spec-compliant clients should prefer MCP Tasks** for long windows:

1. send `tools/call` with `params.task` (or use `McpClient.CallToolAsTaskAsync`)
2. poll `tasks/get`
3. fetch the terminal `CallToolResult` via `tasks/result`
4. cancel via `tasks/cancel`

The legacy fallback remains for clients that do not implement Tasks yet:

- `collect_cpu_sample(..., runAsJob=true)`
- `get_collection_status(handle)`
- `cancel_collection(handle)`

`get_collection_status` also accepts MCP `taskId`s so non-spec clients can read a
Task's state through the legacy tool. Do **not** combine task augmentation with
`runAsJob=true` on the same call.

### Prompts (curated playbooks)

In addition to tools, the server exposes 6 MCP **Prompts** that pre-package the
investigation strategies from [`investigation-playbooks.md`](./investigation-playbooks.md)
so the LLM can opt into a baked recipe instead of re-planning the next call
after every step. Prompts do not consume the tool-slot budget — clients
discover them via `prompts/list` and request a specific one via `prompts/get`.

| Prompt | Source playbook | Required inputs |
|---|---|---|
| `diagnose-high-latency` | "The app feels slow / high latency" | none (all optional: `processId?`, `durationSeconds?`, `symptom?`) |
| `diagnose-memory-growth` | "Memory keeps growing" | none (`processId?`, `windowSeconds?`, `symptom?`) |
| `diagnose-5xx-errors` | "We're seeing 5xxs in production" | none (`processId?`, `symptom?`) |
| `diagnose-slow-outbound-http` | "Slow outbound HTTP calls" | none (`processId?`, `durationSeconds?`, `symptom?`) |
| `triage-nativeaot` | "Is this a NativeAOT app?" | none (`processId?`) |
| `diagnose-safely-in-prod` | "Safest investigation in production" | none (`processId?`) |

Every prompt returns a single `user`-role message whose content is annotated
with `audience: ["assistant"]` so MCP clients that distinguish user-facing
templates from assistant-facing context route them directly into the LLM's
context window. Each prompt embeds the hypothesis tree from the playbook plus
exact tool-call examples (with placeholder args reflecting bootstrap implícito).
The LLM may always ignore a prompt and drive ad-hoc.

### Handle chaining nos coletores (`query_collection`)

Os 5 coletores windowed — `snapshot_counters`, `collect_exceptions`,
`collect_gc_events`, `collect_activities`, `collect_event_source` — devolvem, junto do summary +
top-N inline, um `handle` opaco (Crockford-base32, TTL ~10 min) registrado num
store em memória. A LLM pode então re-projetar o mesmo artefato sob outra
visão **sem rodar o EventPipe de novo** chamando `query_collection`:

```jsonc
// 1. coleta uma vez
collect_exceptions(processId=4242, durationSeconds=10)
  → { summary: "30 exceptions (3 types)", handle: "01H...XY", data: { … top-N } }

// 2. drilldown N vezes dentro da janela TTL
query_collection(handle="01H...XY", view="recent", topN=20)
query_collection(handle="01H...XY", view="byType")
```

Visões disponíveis por `kind`:

| Kind (`CollectionHandleKinds`) | Emitido por | Views aceitas |
|---|---|---|
| `counters` | `snapshot_counters` | `summary` (default), `byProvider` |
| `exception-snapshot` | `collect_exceptions` | `summary` (default = `byType.Take(topN)`), `byType`, `recent` |
| `gc-events` | `collect_gc_events` | `summary` (default), `events`, `pauseHistogram` |
| `activities` | `collect_activities` | `summary` (default), `bySource`, `byOperation`, `activities` |
| `event-source` | `collect_event_source` | `summary` (default), `byEventName`, `events` |

> **Nota — truncação em `event-source`:** o coletor para de armazenar eventos
> ao atingir `maxEvents`, mas continua contando o total. As views
> `summary`/`byEventName` agora trazem `capturedCount` e `truncated`; quando
> `truncated=true` os grupos refletem só o prefixo capturado — re-rode
> `collect_event_source` com `maxEvents` maior pra agregados exatos.

Handles invalidam quando: o TTL expira, o processo alvo morre (evicção
automática), ou um restart do server zera o store. Acesso a handle
desconhecido devolve `DiagnosticError { Kind: "HandleExpired" }` com um
`NextActionHint` apontando o coletor original.

Esse contrato é o equivalente "split collector, unified drilldown"
(documentado em [`AGENTS.md`](../AGENTS.md)) aplicado aos coletores EventPipe
— mesmo padrão que `inspect_dump`/`inspect_live_heap` → `query_heap_snapshot`
e `collect_thread_snapshot` → `query_thread_snapshot`.

### Kernel-side signals (`get_container_signals`)

Mata o blind-spot mais comum em K8s: "app está lento, mas EventCounters dizem
que CPU/memória estão ok" — na maior parte das vezes é **CPU throttling no
cgroup**, invisível pelo runtime. `get_container_signals` lê cgroup v2 +
`/proc/<pid>/oom_score` e devolve:

- `Cpu`: `usage_usec`, `nr_periods`, `nr_throttled`, `throttled_usec`,
  `ThrottlePercent` (canonical signal) e `QuotaCores` (null = unlimited).
- `Memory`: `current`, `max`, `high`, `UsageFraction`, contadores
  `oom_kill` / `max-hit` extraídos de `memory.events`.
- `Pressure` (PSI): `cpu.some.avg10`, `memory.some/full.avg10`, `io.some/full.avg10`.
- `Pids` e `oom_score`.

Tudo best-effort: arquivos faltando (PSI em kernel antigo, sem limite de
memória, container sem read em `memory.events`) viram entradas em `Notes`, não
erro fatal. Em Windows / cgroup v1 / sem cgroup, devolve `InContainer=false`
+ `CgroupVersion` correto e `Notes` explicativo (job-object metrics ainda não
foram wired).

O `get_diagnostic_capabilities` ganhou as flags do kernel-side para você saber
se vale a pena tentar a coleta antes: `InContainer`, `CgroupV2`,
`CanSeeThrottle` (true sse há quota configurada → throttling é observável),
`PsiAvailable`, `PerfInstalled`, `HasCapPerfmon`, `PerfEventParanoid`,
`HasCapSysPtrace`, `PtraceScope` e `EtwKernelOk`. Slice 2b também expõe
**`CanSampleOffCpu`** — true quando o sidecar já cumpre os pré-requisitos do
backend (Linux: perf + privilégio suficiente para `sched_switch`; Windows:
processo elevado). Quando false, `Notes` traz a hint concreta do motivo antes
da LLM tentar `collect_off_cpu_sample` num sidecar sem privilégio.

NextActionHints: throttle > 5% sugere `collect_cpu_sample` direto; memória >
85% do limite sugere `inspect_live_heap` antes do OOM-kill.

### Off-CPU sampling (`collect_off_cpu_sample` + `query_off_cpu_snapshot`)

Complementa o `collect_cpu_sample` (que mostra **on-CPU** — onde o app gasta
tempo executando) com **off-CPU** — onde threads ficaram **bloqueadas**
(I/O, locks, condvars, monitor wait). Resolve o blind-spot clássico "CPU baixa
mas latência alta": sampling on-CPU não enxerga porque as threads não estão
rodando.

- **Linux:** usa `perf record -a -e sched:sched_switch --call-graph dwarf` em
  todo o sistema (o tracepoint `sched_switch` só dispara na thread que sai de
  CPU, então restringir por PID perde o evento de IN). Spans são filtrados
  pós-coleta pelo `/proc/<pid>/task/*` do alvo. Requer `CAP_PERFMON` (kernel
  ≥ 5.8) ou `perf_event_paranoid <= -1`, e `perf` instalado
  (`linux-tools-common` / `linux-tools-$(uname -r)` no Debian/Ubuntu).
  `SymbolSource: "perf-sched-dwarf"`.
- **Windows:** usa a sessão NT Kernel Logger via `TraceEvent` com
  `ContextSwitch + Dispatcher + ImageLoad/Process/Thread`, stack walk no
  `ContextSwitch` (a stack capturada na hora do switch-out é exatamente a
  chamada bloqueante). Wait reason do kernel
  (`UserRequest` / `WrLpcReceive` / `WrQueue`...) vira o `PrevState` do span,
  mirror direto do `S/D/I` do Linux. Spans pendentes ao fim da janela viram
  censored (`IsCensored=true`) com duração lower-bound, igual ao Linux.
  Requer **BUILTIN\\Administrators** ou `SeSystemProfilePrivilege`; sem isso
  devolve `PermissionDenied` com hint apontando os dois caminhos suportados
  (`Administrators` **ou** `Profile system performance`). Pra produção, ver
  [`windows-sidecar-service.md`](./windows-sidecar-service.md)
  (Windows Service com `LocalSystem` ou conta dedicada + privilégio único).
  `SymbolSource: "etw-cswitch-pdb"` (resolve PDBs locais + `_NT_SYMBOL_PATH`).
- **Managed↔kernel stack merge:** ainda não — frames são puramente nativos /
  kernel em ambas as plataformas. Sub-slice 2c.

`collect_off_cpu_sample(pid, durationSeconds=10, topN=10)` devolve `{handle,
summary, top}` com os stacks que mais tempo passaram off-CPU.
`query_off_cpu_snapshot(handle, view, ...)` segue o padrão **split collector,
unified drilldown**: `view="topStacks"` (default), `view="byThread"`
(agregado por TID com `TopBlockingLeaf` + estado dominante), ou
`view="stack"` com `stackRank=N` (1-based) pra exportar o stack completo.


## Quick index

> NativeAOT coverage detail (which symbol source per tool, per OS): see
> [`aot-coverage.md`](./aot-coverage.md).

| Tool | Cost | Requires CoreCLR? | NativeAOT? | Side effects |
|---|---|---|---|---|
| [`list_dotnet_processes`](#list_dotnet_processes) | cheap | no | ✅ | none |
| [`get_process_info`](#get_process_info) | cheap | no | ✅ | none |
| [`get_diagnostic_capabilities`](#get_diagnostic_capabilities) | ~2 s | no | ✅ | opens a short EventPipe probe |
| [`get_container_signals`](#get_container_signals) | cheap | no | ✅ (Linux) | reads `/sys/fs/cgroup` + `/proc` files |
| [`get_memory_trend`](#get_memory_trend) | window-bound | no | ✅ | reads `/proc/<pid>/smaps_rollup` + `/proc/<pid>/stat` (Linux) or `GetProcessMemoryInfo` (Windows) |
| `collect_off_cpu_sample` (Linux/Windows) | window-bound | no | ✅ (Linux) | system-wide `perf record` (Linux) / NT Kernel Logger CSwitch (Windows, admin) |
| `query_off_cpu_snapshot` | cheap | no | ✅ | drilldown on handle from `collect_off_cpu_sample` |
| [`snapshot_counters`](#snapshot_counters) | window-bound | no | ✅ | opens an EventPipe session |
| [`collect_cpu_sample`](#collect_cpu_sample) | window-bound | no | ✅ (perf/ETW, native frames) | EventPipe + temp `.nettrace` on disk |
| [`collect_allocation_sample`](#collect_allocation_sample) | window-bound | no | ⚠️ TypeName empty | EventPipe session |
| [`collect_exceptions`](#collect_exceptions) | window-bound | no | ✅ | EventPipe session |
| [`collect_gc_events`](#collect_gc_events) | window-bound | no | ✅ | EventPipe session |
| [`collect_activities`](#collect_activities) | window-bound | no | ✅ | EventPipe session |
| [`collect_event_source`](#collect_event_source) | window-bound | no | ⚠️ provider must be embedded at publish | EventPipe session |
| `collect_thread_snapshot` / `query_thread_snapshot` | seconds | no | ✅ via `linux-native-stack` / `etw-native-stack` | ptrace attach (Linux) / kernel logger (Windows) |
| `inspect_live_heap` / `inspect_dump` (heap) / `query_heap_snapshot` | seconds | **yes** | ❌ | ClrMD walks managed heap (heap drilldown values metadata-only by default — see [Security gates](#security-gates-b4)) |
| [`collect_process_dump`](#collect_process_dump) | seconds–minutes | no | ✅ (native dump) | **writes a dump file to disk** |
| [`capture_method_bytes`](#capture_method_bytes) | cheap | **yes** | ❌ (use `dotnet-native-mcp.disassemble`) | reads JIT code-heap |
| `list_pods` (orchestrator) | cheap | n/a | n/a | Kubernetes `pods.list` only — **opt-in**, registered only when `Orchestrator:Enabled=true` |

"Window-bound" means the duration is the dominant cost; the tool will block for
~`durationSeconds`.

### Linux runtime requirements

EventPipe-based tools (including `collect_activities`, alongside the ones listed in the index above) only need the
diagnostic IPC socket, which works as long as the MCP server runs as the
**same UID** as the target process. ClrMD-backed tools added since the MVP —
`collect_thread_snapshot`, `inspect_live_heap`, `inspect_dump` against a live
PID, and `collect_process_dump` — additionally call `ptrace(PTRACE_ATTACH, …)`
under the hood. On Linux, matching UIDs is **not** sufficient when the host's
`kernel.yama.ptrace_scope` is `1` (the Debian/Ubuntu/WSL default): the kernel
blocks same-UID peer attach.

If a request lands in that state you'll get a structured error envelope (see
issue #32):

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N." } }
```

Mitigations:

- **Docker:** add `--cap-add SYS_PTRACE` to the **sidecar** container.
- **Kubernetes:** set `capabilities.add: ["SYS_PTRACE"]` on the sidecar
  container's `securityContext` (see [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml)).
- **Bare host / local dev:** `sudo sysctl -w kernel.yama.ptrace_scope=0`, or
  run the MCP server as root.

When ptrace cannot be granted, fall back to `collect_process_dump` +
`inspect_dump` (the dump capture runs in the target's own process, so it does
not require ptrace from the sidecar — but writing the dump file is still
gated on the diagnostic socket UID).

For **NativeAOT on Linux**, `collect_thread_snapshot` now routes to
`eu-stack -p <pid>` (elfutils) instead of ClrMD. The snapshot payload carries
`source: "linux-native-stack"` and maps wait reason from
`/proc/<pid>/task/<tid>/{status,wchan}` (`BlockedOnLock`, `BlockedOnIO`,
`BlockedOnUninterruptibleIO`, `Stopped`, `Running`). This path still requires
same-UID + ptrace gate; when denied the `PermissionDenied` envelope includes a
hint to the perf-replay fallback tracked in issue #92.

---

## `list_dotnet_processes`

Lists every .NET process on the local machine that exposes a Diagnostic IPC
endpoint (Unix socket on Linux, named pipe on Windows).

**Parameters:** none.

**Returns:** array of `DotnetProcess`:

```json
[
  {
    "processId": 12345,
    "commandLine": "/usr/bin/dotnet /app/MyApi.dll",
    "operatingSystem": "linux",
    "processArchitecture": "x64",
    "runtimeVersion": "10.0.0",
    "managedEntrypointAssemblyName": "MyApi"
  }
]
```

**Notes:** processes that respond too slowly or whose IPC endpoint is
unreachable are silently omitted.

---

## `get_process_info`

Returns metadata for a single PID.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** a single `DotnetProcess` (same shape as above) or `null` if the
process is gone / unreachable.

---

## `get_diagnostic_capabilities`

Probes the target by opening a short EventPipe session against the
`Microsoft-DotNETCore-SampleProfiler` provider. The presence/absence of sample
events is used to classify the runtime as **CoreCLR** vs **NativeAOT**.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |

**Returns:** `DiagnosticCapabilities`:

```json
{
  "processId": 12345,
  "runtime": "CoreClr",
  "runtimeVersion": "10.0.0",
  "canReadEventCounters": true,
  "canSampleCpu": true,
  "canCollectGcDump": true,
  "canCollectExceptions": true,
  "canCollectHttpActivity": true,
  "canCollectCustomEventSource": true,
  "canCollectProcessDump": true,
  "notes": "CoreCLR runtime detected via SampleProfiler events."
}
```

**Notes:** always call this **first** in a session. The result tells the LLM
(or human) which other tools can be used on the target. NativeAOT will return
`runtime = "NativeAot"` and `canSampleCpu = false`.

---

## `get_memory_trend`

Samples OS-level memory metrics at regular intervals over a configurable window
and computes per-second deltas and a growth verdict. Works on **any** runtime
(CoreCLR, NativeAOT, even non-.NET processes) — no EventPipe session required.

Use this as a **lightweight memory-leak signal** before reaching for heap dumps.
It answers "is the process growing and how fast?" without walking the heap.

**Sources:**
- **Linux**: `/proc/<pid>/smaps_rollup` (Rss, Pss, Anonymous) and
  `/proc/<pid>/stat` fields 10 & 12 (minflt / majflt). Pure file reads —
  no privileges, no EventPipe.
- **Windows**: `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)`:
  `WorkingSetSize` (RSS), `PrivateUsage` (private committed bytes),
  `PageFaultCount`. Requires `PROCESS_QUERY_INFORMATION` access to the target.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id |
| `durationSeconds` | `int` | `10` | Observation window length in seconds. Must be ≥ 2. |
| `sampleEverySeconds` | `int` | `2` | Interval between consecutive samples in seconds. Must be ≥ 1. |

**Returns:** `MemoryTrend`:

```json
{
  "processId": 12345,
  "windowStart": "2026-05-18T20:00:00Z",
  "windowEnd": "2026-05-18T20:00:10Z",
  "samples": [
    {
      "timestamp": "2026-05-18T20:00:00Z",
      "rssBytes": 104857600,
      "pssBytes": 52428800,
      "privateAnonBytes": 83886080,
      "heapRegionBytes": null,
      "majorFaults": 12,
      "minorFaults": 50000
    }
  ],
  "deltas": {
    "rssBytesPerSec": 1200000.0,
    "pssBytesPerSec": 600000.0,
    "majorFaultsPerSec": 0.2
  },
  "verdict": "growing",
  "notes": []
}
```

**Verdict heuristic:** RSS growth > 1 MiB/s → `growing`; RSS decrease > 1
MiB/s → `shrinking`; otherwise → `stable`. All three values are
stable-but-informative labels — they do not distinguish between heap and
stack allocations.

**Field notes:**
- `pssBytes` is Linux-only (Proportional Set Size — shared pages charged
  proportionally). Always `null` on Windows.
- `heapRegionBytes` is `null` on both platforms (requires a full
  `/proc/<pid>/smaps` walk; omitted for cost reasons).
- On Windows, `majorFaults` is always `0` — Windows does not separate
  major/minor faults; the combined count appears in `minorFaults`.

**Next-action hints:**
- `verdict = "growing"` → suggests `inspect_live_heap` (identify dominant
  retainers) and `get_container_signals` (cross-check against cgroup limits).
- `verdict = "stable"` or `"shrinking"` → suggests `snapshot_counters`.

---

## `snapshot_counters`

Subscribes to one or more EventCounter providers and returns the latest value
seen per counter over a fixed window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `5` | Collection window. Must be ≥ 1. |
| `providers` | `string[]?` | see below | EventSource provider names |
| `intervalSeconds` | `int` | `1` | Per-provider refresh interval |

When `providers` is null/empty the defaults are:
`System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft-AspNetCore-Server-Kestrel`.

**Returns:** `CounterSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:05",
  "counters": [
    {
      "provider": "System.Runtime",
      "name": "cpu-usage",
      "displayName": "CPU Usage",
      "value": 23.4,
      "unit": "%",
      "kind": "Mean"
    }
  ]
}
```

---

## `collect_cpu_sample`

Captures a CPU sample via the `Microsoft-DotNETCore-SampleProfiler` provider,
writes a temporary `.nettrace`, parses it with `TraceLog` and aggregates the
top-N hotspots by inclusive and exclusive sample counts.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Sampling window. ≥ 1. |
| `topN` | `int` | `25` | Maximum hotspots returned. ≥ 1. |
| `resolveSourceLines` | `bool` | `true` | Resolve top hotspots to source file:line via PDB / SourceLink. |
| `symbolPath` | `string?` | `null` | Optional symbol search path used when `resolveSourceLines=true`. **Remote symbol servers are denied by default** (issue #165 / M3): any `srv*http(s)://…` segment must point at a host listed under `Diagnostics:SymbolServerAllowlist`, otherwise the call fails with a `SymbolServerNotAllowed` envelope. Local paths always pass through. See [Security gates](#security-gates-b4). |
| `maxResolvedSources` | `int?` | `topN` | Cap on how many hotspots get source resolution. |
| `resolveMethodInstantiations` | `bool` | `false` | Opt-in ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames. CoreCLR only; on Linux requires `CAP_SYS_PTRACE` (or `ptrace_scope=0`) and briefly suspends the target. |
| `maxResolvedMethodInstantiations` | `int?` | `topN` | Cap on how many hotspots get ClrMD generic-instantiation enrichment. |
| `runAsJob` | `bool` | `false` | Run in the background and poll with `get_collection_status(handle)`. |
| `depth` | `SamplingDepth` | `Summary` | `Summary` returns the top 3 hotspots inline; `Detail` / `Raw` return the requested `topN`. |

**Returns:** `CpuSample`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalSamples": 4218,
  "topHotspots": [
    {
      "frame": { "module": "MyApi", "method": "MyApi.Service.DoWork(int)" },
      "inclusiveSamples": 1820,
      "exclusiveSamples": 320
    }
  ],
  "symbolSource": "ElfDemangled"
}
```

`symbolSource` is populated for **NativeAOT** samples only (see #35) and
reports the aggregate symbol-resolution quality of `topHotspots`:

- `ElfDemangled` — every managed frame went through the demangler. Trust the
  names as-is.
- `ElfMangled` — perf returned managed-looking symbols but demangling did not
  apply (e.g. lookup table missing). Names are still usable but may be `S_P_…`-style.
- `Native` — frames are non-managed (libc / P/Invoke / kernel). Expected for
  threadpool/GC threads.
- `Stripped` — perf returned `[unknown]` or raw addresses; names are not
  actionable. Likely missing build-id / PDB on the host.
- `Mixed` — quality varies across `topHotspots`. Inspect per-frame.
- `Unknown` / omitted — CoreCLR sample (the EventPipe path resolves managed
  names directly; this field does not apply).

**Routing.** `collect_cpu_sample` dispatches based on
`get_diagnostic_capabilities`:

- **CoreCLR (Linux + Windows)** — EventPipe `SampleProfiler` over the
  diagnostic socket; managed frames carry the `(mvid, token)` handoff.
- **NativeAOT / Linux** — system-wide `perf record` (frames are native;
  managed names recovered from the AOT `.symbols.map` sidecar when present).
- **NativeAOT / Windows** — NT Kernel Logger `PerfInfo/SampledProfile` via
  ETW; admin elevation (or `SeSystemProfilePrivilege`) required. Frames are
  native; managed names recovered from the PE export table + PDB.

Confirm the dispatch path up front with `get_diagnostic_capabilities` →
`data.canSampleCpu`. Coverage and AOT caveats are summarized in
[`aot-coverage.md`](./aot-coverage.md).

**NativeAOT/Linux perf install.** On Debian/Ubuntu/WSL the distro ships a
wrapper at `/usr/bin/perf` that fails unless the matching
`linux-tools-$(uname -r)` package is installed. The sampler auto-discovers a
working binary by probing `/usr/lib/linux-tools-*/perf` (kernel-matched first,
then newest-first); when nothing usable is found, `IsAvailable` returns false
and the tool reports `not_supported`. Install with:

```bash
sudo apt install linux-tools-$(uname -r) linux-tools-generic
```

**Sampling rate** is the runtime default (~1 kHz). A 10-second window typically
yields a few thousand samples; bump `durationSeconds` for sparse workloads.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Prefer task-augmented `tools/call` + `tasks/get`/`tasks/result`
when your client supports the spec; otherwise use the legacy
`runAsJob=true` + `get_collection_status(handle)` fallback.

## Symbol resolution

Tools that resolve external symbols now share the same precedence chain:

1. explicit tool parameter `symbolPath`
2. server startup env `MCP_SYMBOL_PATH`
3. host env `_NT_SYMBOL_PATH`
4. local fallback paths (typically the target `MainModule` directory; `collect_cpu_sample`
   also appends module directories discovered in the trace)

`symbolPath` values use TraceEvent / `SymbolReader`'s NT-style syntax on every OS.
Common examples:

- `srv*C:\\symbols*https://msdl.microsoft.com/download/symbols`
- `cache*/tmp/sym;srv*https://nuget.smbsrc.net`
- local PDB-only default: omit `symbolPath` and keep the PDB next to the target binary

The same override shape is exposed by `collect_cpu_sample`, `collect_off_cpu_sample`,
`collect_thread_snapshot`, `inspect_dump`, and `inspect_live_heap`.

**Opt-in closed generics (`resolveMethodInstantiations`).** On Linux, EventPipe alone only knows the
open `MethodDef` for generic methods like `Echo<T>`. When you enable this flag, the server performs
an additional ClrMD attach after the trace ends, resolves the hottest instruction pointers back to
closed runtime methods, and stamps `MethodIdentity.ClosedSignature` plus
`MethodIdentity.GenericTypeArguments.Method`. This keeps the default EventPipe path lightweight while
making LINQ / MediatR / serializer hotspots far more operator-friendly when you explicitly need the
closed form.

---

## `collect_allocation_sample`

Captures allocation samples from the target process via `GCAllocationTick`
events from `Microsoft-Windows-DotNETRuntime` (keyword `GCKeyword=0x1`, level
Verbose). The GC fires this event roughly every **100 KB of total managed
allocations** and carries the TypeName of the most recently allocated object
plus a call stack. The call stack is accessible via `get_call_tree` using the
handle returned by this tool.

**CoreCLR**: TypeName is fully populated with managed type names. The call tree
resolves to managed method names via rundown events. `MethodIdentity` (MVID +
metadata token) is emitted for top-N frames, enabling the assembly-mcp handoff.

**NativeAOT**: `GCAllocationTick` events fire, but the runtime **does not**
populate the `TypeName` field — managed type metadata is stripped at compile
time. All events roll up under `<unknown>`. The call tree is captured but
contains native frame addresses only. See [`aot-coverage.md`](./aot-coverage.md)
for the full NativeAOT diagnostic matrix.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id (optional — auto-selects when only one .NET process is visible) |
| `durationSeconds` | `int` | `10` | Sampling window. Must be ≥ 1. |
| `topN` | `int` | `25` | Maximum types per ranked list. Must be ≥ 1. |

**Returns:** `AllocationSample` with a drilldown `handle`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 14250,
  "totalBytes": 1469161472,
  "topByBytes": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" },
    { "typeName": "System.Byte[]", "totalBytes": 60000000, "eventCount": 200, "dominantKind": "Large" }
  ],
  "topByCount": [
    { "typeName": "System.String", "totalBytes": 1400000000, "eventCount": 14000, "dominantKind": "Small" }
  ]
}
```

`TopByBytes` ranks by total allocated bytes — the dominant signal for allocation
pressure. `TopByCount` ranks by sampling event count — useful when many small
types compete with one large-object type.

**Notes on sampling semantics:** `GCAllocationTick` is a sampled event, not
an instrumented one. It samples the *most recently allocated* type when the
total allocation counter crosses each 100 KB threshold. High-frequency types
are sampled proportionally more often, making the top-N ranking statistically
accurate for steady workloads.

**Run after** `snapshot_counters` shows elevated `gen-0-gc-count`,
`gen-1-gc-count`, or growing `gc-heap-size`. Use `get_call_tree` with the
returned handle to find which allocation sites are responsible.

---

## `collect_exceptions`

exception thrown by the process during the window.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxRecent` | `int` | `100` | Maximum exception details to return |

**Returns:** `ExceptionSnapshot`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalExceptions": 42,
  "byType": [
    { "exceptionType": "System.InvalidOperationException", "count": 30 },
    { "exceptionType": "System.TimeoutException", "count": 12 }
  ],
  "recent": [
    {
      "timestamp": "2026-05-18T20:00:01.123Z",
      "exceptionType": "System.InvalidOperationException",
      "exceptionMessage": "Sequence contains no elements",
      "exceptionHResult": "0x80131509",
      "threadId": 17
    }
  ],
  "recentCap": 100
}
```

**Notes:** also catches "first-chance" exceptions caught by the app — useful
for detecting error rates much higher than the response logs suggest.

`totalExceptions` and `byType` are always exact for the window. `recent` is
capped to `maxRecent` (default `100`, echoed back as `recentCap`); when
`totalExceptions > recentCap` it contains the first `recentCap` exceptions
observed, not a random sample. Raise `maxRecent` for storms where the tail
matters; lower it when you only want a quick signal.

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call`; legacy
clients can still fall back to polling through `get_collection_status(taskId)`
if they need a tool-shaped status check.

---

## `collect_gc_events`

Subscribes to the runtime `GC` keyword, pairs `GCStart`/`GCStop` events and
returns aggregate + per-collection details.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `durationSeconds` | `int` | `10` | Window length |
| `maxEvents` | `int` | `200` | Cap on individual GC events returned |

**Long-running pattern:** this tool supports MCP Tasks (`execution.taskSupport:
"optional"`). Spec clients should use task-augmented `tools/call`; legacy
clients can still read a task via `get_collection_status(taskId)` when needed.

**Returns:** `GcSummary`:

```json
{
  "processId": 12345,
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalCollections": 18,
  "totalPauseTime": "00:00:00.0420000",
  "maxPauseTime": "00:00:00.0150000",
  "generations": [
    { "generation": 0, "count": 14 },
    { "generation": 1, "count": 3 },
    { "generation": 2, "count": 1 }
  ],
  "events": [
    {
      "timestamp": "2026-05-18T20:00:01.500Z",
      "generation": 0,
      "reason": "AllocSmall",
      "type": "NonConcurrentGC",
      "pauseDuration": "00:00:00.0021000"
    }
  ]
}
```

**Notes:** to capture a full gcdump (heap snapshot), use `collect_process_dump`
with `dumpType = "WithHeap"` and analyze offline with `dotnet-dump`.

---

## `collect_activities`

Captures `ActivitySource` spans through the `Microsoft-Diagnostics-DiagnosticSource`
EventPipe bridge, keeping completed span records inline and grouped rollups behind
`query_collection`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `sources` | `string[]?` | `null` | Optional `ActivitySource` filters (`*` / `?` wildcards supported) |
| `durationSeconds` | `int` | `10` | Window length |
| `maxActivities` | `int` | `200` | Cap on captured span records retained inline + in the handle artifact |

**Returns:** `ActivityCapture`:

```json
{
  "processId": 12345,
  "sourceFilters": ["MyCompany.Checkout*"],
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalActivities": 12,
  "completedActivities": 12,
  "activities": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "id": "00-3b2dc9c6a0b7dc27ba8e290f198d98f4-9f10a33a49390375-01",
      "parentId": null,
      "traceId": "3b2dc9c6a0b7dc27ba8e290f198d98f4",
      "spanId": "9f10a33a49390375",
      "parentSpanId": null,
      "startedAt": "2026-05-18T20:00:00.120Z",
      "stoppedAt": "2026-05-18T20:00:00.188Z",
      "duration": "00:00:00.0680000",
      "tags": { "http.method": "POST", "db.system": "sqlserver" }
    }
  ],
  "bySource": [
    {
      "sourceName": "MyCompany.Checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ],
  "byOperation": [
    {
      "sourceName": "MyCompany.Checkout",
      "operationName": "POST /checkout",
      "count": 12,
      "completedCount": 12,
      "averageDurationMs": 32.7,
      "maxDurationMs": 68.0
    }
  ]
}
```

**Drilldown:** `query_collection(handle, view="bySource" | "byOperation" | "activities")`
re-projects the same capture window without reopening EventPipe.

**Notes:**

- The collector listens to `Activity/Stop` bridge events, so every returned row is a
  completed span with duration + tags already populated.
- `sources` matches `ActivitySource.Name`, not operation names.
- The provider supports a single Activity listener per session; this tool claims it for
  the duration of the capture window.

---

## `collect_event_source`

Generic passthrough that opens an EventPipe session for any EventSource by
name and captures the events it emits in the window. Use for HTTP activity
(`System.Net.Http`), Kestrel/Hosting/Logging events, or app-defined sources.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `providerName` | `string` | — | EventSource provider name. **Must be on the curated allowlist** (issue #165 / M2) — see [Security gates](#security-gates-b4); the deny path returns an `EventSourceProviderNotAllowed` envelope listing the curated set. |
| `durationSeconds` | `int` | `10` | Window length |
| `keywords` | `long` | `-1` | Keyword mask. `-1` = all (clamped to `0` for opt-in non-allowlisted providers when left at `-1`). |
| `eventLevel` | `int` | `5` | 0=LogAlways…5=Verbose (clamped to `4` for opt-in non-allowlisted providers when left above `4`). |
| `maxEvents` | `int` | `200` | Cap on captured events |
| `unsafeProvider` | `bool` | `false` | Opt-in for non-allowlisted providers (issue #165 / M2). Only honoured when the server has `Diagnostics:AllowSensitiveHeapValues=true`. |

**Returns:** `EventSourceCapture`:

```json
{
  "processId": 12345,
  "provider": "System.Net.Http",
  "startedAt": "2026-05-18T20:00:00Z",
  "duration": "00:00:10",
  "totalEvents": 128,
  "events": [
    {
      "timestamp": "2026-05-18T20:00:00.500Z",
      "provider": "System.Net.Http",
      "eventName": "RequestStart",
      "level": "Informational",
      "payload": { "scheme": "https", "host": "api.example.com", "port": "443" }
    }
  ]
}
```

**Tips:**

- `System.Net.Http` — outbound HTTP request/response timing
- `Microsoft.AspNetCore.Hosting` — request pipeline events
- `Microsoft-AspNetCore-Server-Kestrel` — connection lifecycle
- `Microsoft-Extensions-Logging` — structured app logs flowing through ILogger

---

## `collect_process_dump`

Writes a process dump to disk via the diagnostic IPC channel.

> **Sandbox (issue #163).** `outputDirectory` is interpreted as a **relative**
> sub-path under the operator-configured artifact root. The root is set by the
> `MCP_ARTIFACT_ROOT` environment variable (default
> `{TempPath}/dotnet-diagnostics-mcp`). Absolute paths, `..` traversal, and
> symlink escapes are rejected with a structured `InvalidArtifactPath` error.
> Files are written with POSIX mode `0600`; the parent directory is `0700`.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `dumpType` | `string` | `"Mini"` | `Mini` / `Triage` / `WithHeap` / `Full` |
| `outputDirectory` | `string?` | artifact root | **Relative** sub-path under `MCP_ARTIFACT_ROOT`. Must not be absolute. |

**Returns:** `DumpResult`:

```json
{
  "processId": 12345,
  "dumpType": "Mini",
  "filePath": "/tmp/dotnet-diagnostics-mcp/dump_pid12345_Mini_20260518T200000Z.dmp",
  "fileSizeBytes": 28311552,
  "createdAt": "2026-05-18T20:00:00Z"
}
```

**Cost / size:**

| Type | Approx. size for a 200 MB workload | Use when |
|---|---|---|
| `Mini` | ~30 MB | crash triage, thread state |
| `Triage` | ~30 MB | minimal, strings stripped |
| `WithHeap` | full workload + heap (200+ MB) | leak/heap investigation |
| `Full` | largest | last resort, full address space |

**Side effects:** **writes to disk** on the server. In a sidecar topology the
file lives on the sidecar container's filesystem — mount a PVC if you expect
to capture more than transient dumps.

## `capture_method_bytes`

Reads the JIT-emitted (or ReadyToRun-baked) native machine code for a single
managed method out of a live .NET process (or `WithHeap`/`Full` dump) and
writes the raw bytes to a file on disk. Closes the only disasm coverage gap:
NativeAOT and R2R binaries live on disk and are already covered by
`dotnet-native-mcp`; JIT-emitted code lives only in the target process memory.

The bytes are emitted via a **file side-channel** (mirroring `collect_process_dump`)
so binary payloads never enter the LLM context. Each captured region returns a
`NextActionHint` for `dotnet-native-mcp.disassemble(rawBlob=true)` carrying the
file path, size, architecture and load-base — feed that hint verbatim to
disassemble.

**Backend:** ClrMD `HotColdInfo`. **Requires:** CoreCLR target (NativeAOT
returns an error envelope — use `dotnet-native-mcp.load_native_binary` against
the binary on disk instead). On Linux also requires `CAP_SYS_PTRACE` for live
attach.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `moduleVersionId` | `string` (GUID) | — | MVID of the method's declaring module (from a sampler hotspot's `MethodIdentity`) |
| `metadataToken` | `string` | — | MethodDef token (`0x06000123` or decimal) |
| `processId` | `int?` | auto-select | Live PID. Mutually exclusive with `dumpFilePath` |
| `dumpFilePath` | `string?` | — | Path to a `WithHeap`/`Full` dump. Mutually exclusive with `processId` |
| `codeAddress` | `string?` | — | Optional native IP (hex or decimal) for the fast `GetMethodByInstructionPointer` path; verified against `(mvid, token)` |
| `tier` | `string?` | — | Informational label (`Tier0`/`Tier1`/etc.) echoed into the output file name. ClrMD does not expose tier metadata, so this is **not** a filter |
| `outputDirectory` | `string?` | `method-bytes/{pid}` | **Relative** sub-path under `MCP_ARTIFACT_ROOT` (default `{TempPath}/dotnet-diagnostics-mcp`). Same sandbox rules as `collect_process_dump`: absolute paths, `..` traversal, and symlink escapes are rejected with `InvalidArtifactPath`. `.bin` files are written `0600`. |

**Returns:** `CapturedMethodBytes`:

```json
{
  "origin": "Live",
  "processId": 12345,
  "runtimeName": "coreclr",
  "runtimeVersion": "10.0.0",
  "architecture": "X64",
  "method": { "moduleVersionId": "…", "metadataToken": 100663297, "methodName": "…", "typeFullName": "…" },
  "regions": [
    { "filePath": "/tmp/…/My.Type.Method-Hot--0x06000001.bin", "size": 412, "baseAddress": 140234567890, "architecture": "X64", "region": "Hot", "tier": null, "compilationType": "Jit" }
  ],
  "outputDirectory": "/tmp/…",
  "warnings": []
}
```

**Handoff:** every region carries a `NextActionHint` for
`dotnet-native-mcp.disassemble` with `imagePath`, `rawBlob: true`, `rva: 0`,
`size`, `architecture` and `baseAddress` — pass those through unchanged.

**Side effects:** writes one `.bin` file per region (Hot, plus Cold when the
JIT split the method). Suspend window on live attach is typically < 100 ms.
**NativeAOT/R2R targets are rejected** with an explanatory error envelope.

---

## Security gates (B4)

Issue #165 introduced three opt-in security gates that change the default behaviour of
`query_heap_snapshot`, `collect_event_source` and `collect_cpu_sample`. All three are bound
from the `Diagnostics:` configuration section and can be set via env vars
(`Diagnostics__AllowSensitiveHeapValues=true`, `Diagnostics__EventSourceAllowlist__0=…`,
`Diagnostics__SymbolServerAllowlist__0=msdl.microsoft.com`).

### H4 — heap drilldown defaults to metadata-only

`query_heap_snapshot` with `view=duplicate-strings` and `view=object` no longer returns raw
string previews or field/array element values by default. Instead each value site is replaced
with `<redacted:metadata-only>` and the LLM gets length / type / address metadata only.

To opt-in:

1. set `Diagnostics:AllowSensitiveHeapValues=true` on the server, **and**
2. pass `includeSensitiveValues=true` on the per-call invocation.

When both are present the values flow through `SensitiveDataRedactor`, which replaces any
substring matching the default patterns (Bearer/Basic tokens, JWT-shaped triples,
`password=`/`secret=`/`api_key=` query-string syntax, AWS access keys, GitHub PATs, PEM
blocks) with `<redacted:sensitive>`. Add custom patterns via
`Diagnostics:RedactionPatterns[]`.

The `heap-snapshot://` MCP resource projection is **always metadata-only** — it has no
per-call opt-in surface, so the server flag alone cannot unlock raw values through that
path. Operators who need the redacted-but-present view should call
`query_heap_snapshot view=duplicate-strings includeSensitiveValues=true` (which honours
both gates).

### M2 — `collect_event_source` provider allowlist

Arbitrary user-defined EventSource providers were the easiest way for an attacker who
gained MCP access to siphon application-defined logging (which routinely contains tokens,
PII, SQL parameters). The tool now refuses any `providerName` that is not on the curated
default allowlist (System.Net.Http, Microsoft.AspNetCore.Hosting,
Microsoft-AspNetCore-Server-Kestrel, Microsoft-Extensions-Logging,
Microsoft-Windows-DotNETRuntime, System.Threading.Tasks.TplEventSource, …) or under
`Diagnostics:EventSourceAllowlist[]`.

To capture a custom provider:

- add it to `Diagnostics:EventSourceAllowlist[]` (preferred — survives across calls), or
- set `Diagnostics:AllowSensitiveHeapValues=true` on the server **and** pass
  `unsafeProvider=true` on the call. On that path `keywords=-1` is clamped to `0` and
  `eventLevel>4` is clamped to `Informational` unless the caller passed explicit safer values.

### M3 — symbol-server SSRF guard

`symbolPath` historically accepted any `srv*http(s)://…` segment, which let a malicious
caller turn the sidecar into an outbound HTTP client to any host on the cluster network.
Caller-supplied `symbolPath` values are now parsed and every `srv*` / `symsrv*` segment's
`http://` / `https://` URL must host-match `Diagnostics:SymbolServerAllowlist[]`. Local
filesystem paths and bare directory entries always pass through. The deny path returns a
`SymbolServerNotAllowed` envelope. Tools covered:

- `collect_cpu_sample`
- `collect_off_cpu_sample`
- `collect_thread_snapshot`
- `inspect_dump`
- `inspect_live_heap`

`MCP_SYMBOL_PATH` and `_NT_SYMBOL_PATH` from the **operator-set environment** are *not*
validated — they are treated as trusted by the deployment.
