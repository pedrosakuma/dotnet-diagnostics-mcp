# Bad-code scenarios — exercising dotnet-dbg-mcp end-to-end

`samples/BadCodeSample/` is a minimal API where every endpoint triggers a
different, well-known .NET anti-pattern. Use it to validate that the MCP
server (and an LLM driving it) can pinpoint each problem using nothing but the
9 tools.

## Topology used for these scenarios

We run **three** containers in the same Docker network, with the sample +
sidecar sharing a PID namespace and `/tmp` (mirroring the K8s sidecar):

```bash
docker network create dbgmcp-net 2>/dev/null || true
docker volume  create badcode-tmp >/dev/null

docker run -d --name badcode --network dbgmcp-net \
  -v badcode-tmp:/tmp \
  -p 18180:8080 \
  badcode-sample:dev

docker run -d --name badcode-mcp --network dbgmcp-net \
  --pid=container:badcode \
  -v badcode-tmp:/tmp \
  --user 0 \
  -e MCP_BEARER_TOKEN=dev-token \
  -p 18887:8080 \
  dotnet-dbg-mcp:dev
```

Trigger a scenario from your shell (the `badcode` container exposes the API on
`http://127.0.0.1:18180`) **at the same time** as you collect from the MCP
sidecar (`http://127.0.0.1:18887`).

The sample PID inside the shared namespace is `1` (it owns the container).

## The 7 scenarios

Each row lists: the symptom the user/LLM would report, the endpoint that
reproduces it, the MCP tools that would identify it, and what to look for in
the output.

| # | Symptom | Trigger | Primary tool(s) | Expected signal |
|---|---|---|---|---|
| 1 | "CPU pegged at 100% on one core" | `GET /cpu-burn?ms=3000` | `snapshot_counters`, `collect_cpu_sample` | `cpu-usage` near 100% during the burn; top sampled frames in `System.Security.Cryptography.SHA256` |
| 2 | "Memory keeps growing" | repeated `GET /leak?mb=4` | `snapshot_counters` over time, `collect_gc_events`, `collect_process_dump` | `gc-heap-size` and `working-set` climb monotonically; gen-2 collections increase; dump shows large `byte[]` retained by `leakedBuffers` |
| 3 | "First-chance exception storms in logs" | `GET /exceptions?count=2000` | `snapshot_counters`, `collect_exceptions` | `exception-count` rate jumps; collector returns 100% `FormatException` ("Input string was not in a correct format") |
| 4 | "Requests time out under load even though CPU is low" | `GET /sync-over-async?n=40` | `snapshot_counters`, `collect_cpu_sample` | `threadpool-queue-length` grows, `threadpool-thread-count` climbs slowly; CPU low; sampled stacks show `GetAwaiter().GetResult` / `Task.Wait` frames |
| 5 | "Throughput drops as concurrency grows" | `GET /lock-contention?threads=64&ms=4000` | `snapshot_counters`, `collect_cpu_sample` | `monitor-lock-contention-count` jumps to thousands/sec; stacks dominated by `Monitor.Enter` / `SpinWait` |
| 6 | "GC pauses are frequent in production" | repeated `GET /loh-alloc?count=200` | `snapshot_counters`, `collect_gc_events` | `loh-size` and `gen2-gc-count` rise; collector reports gen-2 collections with `LowMemory` / `Induced` reasons (or just frequent gen-2) |
| 7 | "Outbound HTTP calls are slow" | `GET /slow-http?url=https://httpbin.org/delay/3` | `collect_event_source` `name=System.Net.Http`, `snapshot_counters` with `System.Net.Http` | EventSource emits `Request*/Response*` events with latency between them; `requests-started-rate` and `current-requests` visible in counters |

## How an LLM should drive this

A useful system message for the LLM (already encoded in
`investigation-playbooks.md`) is:

1. **Discover**: `list_dotnet_processes` → pick the target PID
2. **Probe**: `get_process_info` + `get_diagnostic_capabilities` so the LLM
   knows whether stack sampling is available (CoreCLR vs NativeAOT)
3. **Cheap signal**: `snapshot_counters` with `System.Runtime` for 5–10s to
   classify the symptom (CPU? memory? exceptions? threads?)
4. **Targeted capture** based on what the counters showed:
   - CPU high → `collect_cpu_sample`
   - Memory growing → `collect_gc_events`, then `collect_process_dump` if a
     dump is justified
   - Exception count spiking → `collect_exceptions`
   - Latency on outbound HTTP → `collect_event_source` `System.Net.Http`
5. **Report**: aggregate the captured artifacts into a root cause + fix.

## What this proves

If a model — even without seeing the source — can:

- explain *why* `/cpu-burn` is hot (SHA256 in a tight loop),
- spot the leaking list behind `/leak`,
- name `FormatException` as the dominant exception type,
- point at the sync-over-async pattern from sampled stacks,
- correlate `monitor-lock-contention-count` with the shared `lock(...)` block,

…then the MCP surface is good enough for an end-to-end diagnostic loop. That
is the target benchmark for this project; track which scenarios any given
model fails so we can decide whether to add more tools (e.g. lock-contention
EventPipe specifics, allocation sampling, thread stack dump beyond CPU).
