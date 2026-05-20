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

Os 4 coletores windowed — `snapshot_counters`, `collect_exceptions`,
`collect_gc_events`, `collect_event_source` — devolvem, junto do summary +
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

O `get_diagnostic_capabilities` ganhou 3 flags pra você saber se chamar vale a
pena antes: `InContainer`, `CgroupV2`, `CanSeeThrottle` (true sse há quota
configurada → throttling é observável). Slice 2b adicionou
**`CanSampleOffCpu`** — true quando o sidecar tem perf + `CAP_PERFMON` (Linux)
ou está elevado (Windows). Quando false, `Notes` traz a hint concreta de
elevação / capability antes da LLM tentar `collect_off_cpu_sample` num sidecar
sem privilégio.

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
  Requer **elevação administrativa** (ou `SeSystemProfilePrivilege`); sem isso
  devolve `InvalidOperation` com hint pra rodar o sidecar como Administrator.
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

| Tool | Cost | Requires CoreCLR? | Side effects |
|---|---|---|---|
| [`list_dotnet_processes`](#list_dotnet_processes) | cheap | no | none |
| [`get_process_info`](#get_process_info) | cheap | no | none |
| [`get_diagnostic_capabilities`](#get_diagnostic_capabilities) | ~2 s | no | opens a short EventPipe probe |
| [`get_container_signals`](#get_container_signals) | cheap | no | reads `/sys/fs/cgroup` + `/proc` files |
| `collect_off_cpu_sample` (Linux/Windows) | window-bound | no | system-wide `perf record` (Linux) / NT Kernel Logger CSwitch (Windows, admin) |
| `query_off_cpu_snapshot` | cheap | no | drilldown on handle from `collect_off_cpu_sample` |
| [`snapshot_counters`](#snapshot_counters) | window-bound | no | opens an EventPipe session |
| [`collect_cpu_sample`](#collect_cpu_sample) | window-bound | **yes** | EventPipe + temp `.nettrace` on disk |
| [`collect_allocation_sample`](#collect_allocation_sample) | window-bound | no | EventPipe session |
| [`collect_exceptions`](#collect_exceptions) | window-bound | no | EventPipe session |
| [`collect_gc_events`](#collect_gc_events) | window-bound | no | EventPipe session |
| [`collect_event_source`](#collect_event_source) | window-bound | no | EventPipe session |
| [`collect_process_dump`](#collect_process_dump) | seconds–minutes | no | **writes a dump file to disk** |

"Window-bound" means the duration is the dominant cost; the tool will block for
~`durationSeconds`.

### Linux runtime requirements

EventPipe-based tools (the ones listed in the index above) only need the
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

**Requires CoreCLR.** Returns `not_supported` style behaviour (empty samples) on
NativeAOT — confirm via `get_diagnostic_capabilities` first.

**NativeAOT fallback** uses the Linux `perf` profiler. On Debian/Ubuntu/WSL the
distro ships a wrapper at `/usr/bin/perf` that fails unless the matching
`linux-tools-$(uname -r)` package is installed. The sampler auto-discovers a
working binary by probing `/usr/lib/linux-tools-*/perf` (kernel-matched first,
then newest-first); when nothing usable is found, `IsAvailable` returns false
and the tool reports `not_supported`. Install with:

```bash
sudo apt install linux-tools-$(uname -r) linux-tools-generic
```

**Sampling rate** is the runtime default (~1 kHz). A 10-second window typically
yields a few thousand samples; bump `durationSeconds` for sparse workloads.

---

## `collect_allocation_sample`

Captures allocation samples from the target process via `GCAllocationTick`
events from `Microsoft-Windows-DotNETRuntime` (keyword `GCKeyword=0x1`, level
Verbose). The GC fires this event roughly every **100 KB of total managed
allocations** and includes the TypeName of the most recently allocated object.

Unlike `inspect_live_heap` (which requires ClrMD and is unavailable on
NativeAOT), this tool works on **both CoreCLR and NativeAOT** — making it the
primary answer to "who is allocating?" on AOT deployments. See
[`aot-coverage.md`](./aot-coverage.md) for the full NativeAOT diagnostic matrix.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int?` | auto | Target process id (optional — auto-selects when only one .NET process is visible) |
| `durationSeconds` | `int` | `10` | Sampling window. Must be ≥ 1. |
| `topN` | `int` | `25` | Maximum types per ranked list. Must be ≥ 1. |

**Returns:** `AllocationSample`:

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
`gen-1-gc-count`, or growing `gc-heap-size`.

---

## `collect_exceptions`

Subscribes to the runtime `Exception` keyword and captures every managed
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

## `collect_event_source`

Generic passthrough that opens an EventPipe session for any EventSource by
name and captures the events it emits in the window. Use for HTTP activity
(`System.Net.Http`), Kestrel/Hosting/Logging events, or app-defined sources.

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `providerName` | `string` | — | EventSource provider name |
| `durationSeconds` | `int` | `10` | Window length |
| `keywords` | `long` | `-1` | Keyword mask. `-1` = all |
| `eventLevel` | `int` | `5` | 0=LogAlways…5=Verbose |
| `maxEvents` | `int` | `200` | Cap on captured events |

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

**Parameters:**

| Name | Type | Default | Description |
|---|---|---|---|
| `processId` | `int` | — | Target process id |
| `dumpType` | `string` | `"Mini"` | `Mini` / `Triage` / `WithHeap` / `Full` |
| `outputDirectory` | `string?` | `<temp>/dotnet-diagnostics-mcp` | Where to write the file |

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
| `outputDirectory` | `string?` | `<temp>/dotnet-diagnostics-mcp` | Where to write the `.bin` files |

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
