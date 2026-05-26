# dotnet-diagnostics-mcp

[![CI](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml)

An **MCP server** for LLM-driven performance diagnostics on **.NET 10** applications — zero instrumentation required.

> **Status:** 15 unified tools, HTTP + stdio transports, IoT-style triage (6+ steps → 2 steps).
> See [`docs/`](./docs) for full reference.

---

## Table of Contents

- [Quick Start](#quick-start)
- [Install](#install)
- [Tools Overview](#tools-overview)
- [Documentation](#documentation)
- [Goals](#goals)
- [Build & Test](#build--test)
- [Roadmap](#roadmap)

---

## Quick Start

**One call to understand your app's health:**

```bash
# MCP call
inspect_process(view="triage")
```

**Response:**
```json
{
  "verdict": "threadpool-starvation",
  "severity": "Critical",
  "topIndicators": [
    {"name": "threadpool-queue-length", "value": 1191, "score": 100, "level": "critical"},
    {"name": "cpu-usage", "value": 0.13, "score": 0, "level": "normal"},
    {"name": "time-in-gc", "value": 0, "score": 0, "level": "normal"}
  ],
  "hints": [{"nextTool": "collect_events", "suggestedArguments": {"kind": "threadpool"}}]
}
```

**Verdicts:** `cpu-bound`, `gc-pressure`, `threadpool-starvation`, `lock-contention`, `io-bound`, `healthy`

**TopIndicators** are always returned (even when healthy) — enabling **proactive optimization**, not just reactive firefighting. The LLM simply follows the first hint.

---

## Install

Three distributions — pick by environment. Full walkthrough: [`docs/consumer-install.md`](./docs/consumer-install.md)

```bash
# .NET global tool (requires .NET 10 SDK)
dotnet tool install -g dotnet-diagnostics-mcp
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787

# Container (no SDK needed)
docker run -d -p 127.0.0.1:8787:8080 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest

# Self-contained binary — see Releases page
```

<details>
<summary><strong>Transport options</strong></summary>

| Transport | Use case | Auth |
|-----------|----------|------|
| **stdio** | Local dev (Copilot CLI, Claude Desktop) | None (OS-level trust) |
| **HTTP** | Sidecar, shared host, multi-client | Bearer token |

</details>

<details>
<summary><strong>Linux ptrace note</strong></summary>

On Debian/Ubuntu/WSL, `kernel.yama.ptrace_scope=1` blocks ClrMD tools. Fix: `echo 0 | sudo tee /proc/sys/kernel/yama/ptrace_scope`. See [`docs/consumer-install.md`](./docs/consumer-install.md#15-linux-enabling-clrmd-backed-tools-ptrace).

</details>

<details>
<summary><strong>Joint with dotnet-assembly-mcp</strong></summary>

For decompilation + call graphs:

```bash
export ASSEMBLIES_DIR=/path/to/binaries
docker compose -f deploy/docker-compose.yml up -d
```

</details>

---

## Tools Overview

| Tool | Purpose |
|---|---|
| `inspect_process(view="triage")` | **IoT-style diagnosis** — 1 call returns verdict + severity + ranked TopIndicators + hints |
| `inspect_process(view="list")` / `inspect_process(view="info")` | Discover .NET processes via diagnostic IPC |
| `inspect_process(view="capabilities")` | Detect CoreCLR vs NativeAOT and what's usable |
| `inspect_process(view="container")` | Linux cgroup v2: CPU throttling, memory pressure, OOM kills, PSI |
| `collect_events(kind="counters")` | EventCounters with **auto-hints** (CPU, GC, starvation, contention, allocation, I/O) |
| `collect_sample(kind="cpu")` / `query_snapshot(view="call-tree")` | Top-N CPU hotspots (inclusive/exclusive) + on-demand caller→callee tree |
| `collect_sample(kind="off_cpu")` / `query_snapshot` | Where threads block (futex / IO / sleep) — Linux `perf` backend |
| `collect_events(kind="exceptions")` | Managed exceptions thrown in a window, aggregated by type |
| `collect_events(kind="gc")` | GC pauses + per-generation counts |
| `collect_events(kind="activities")` / `query_snapshot` | ActivitySource span capture (trace/span ids, parent linkage, tags, duration) + re-project artifacts |
| `collect_events(kind="event_source")` / `query_snapshot` | Generic EventSource passthrough (HTTP, Kestrel, custom) + re-project artifacts |
| `collect_thread_snapshot` / `query_snapshot` | Managed thread states + SyncBlock lock graph + deadlock / unique-stack drilldown |
| `inspect_heap(source="live")` / `inspect_heap(source="dump")` / `query_snapshot` | Top retained types + retention paths + roots + async state machines, live or from a dump |
| `collect_process_dump` | Write a Mini / Triage / WithHeap / Full dump to disk |
| `start_investigation` | Structured plan (cold / warm / hypothesis) before any collector runs |
| `export_investigation_summary` / `compare_to_baseline` | Portable JSON memory; LLM persists, diffs across deploys |

---

## Documentation

| Doc | Contents |
|-----|----------|
| [`docs/tool-reference.md`](./docs/tool-reference.md) | Full schemas and return shapes |
| [`docs/investigation-playbooks.md`](./docs/investigation-playbooks.md) | Common investigation recipes |
| [`docs/aot-coverage.md`](./docs/aot-coverage.md) | NativeAOT coverage matrix |
| [`docs/client-setup.md`](./docs/client-setup.md) | MCP client configuration |
| [`docs/consumer-install.md`](./docs/consumer-install.md) | Full install walkthrough |

**Deployment guides:**

| Platform | Guide |
|----------|-------|
| Kubernetes sidecar | [`deploy/k8s/README.md`](./deploy/k8s/README.md) |
| Helm chart | [`deploy/helm/README.md`](./deploy/helm/README.md) |
| Azure | [`deploy/azure/README.md`](./deploy/azure/README.md) |
| AWS | [`deploy/aws/README.md`](./deploy/aws/README.md) |
| GCP | [`deploy/gcp/README.md`](./deploy/gcp/README.md) |

<details>
<summary><strong>MCP Prompts (curated playbooks)</strong></summary>

6 built-in prompts: `diagnose-high-latency`, `diagnose-memory-growth`, `diagnose-5xx-errors`, `diagnose-slow-outbound-http`, `triage-nativeaot`, `diagnose-safely-in-prod`. See [Prompts section](./docs/tool-reference.md#prompts-curated-playbooks).

</details>

<details>
<summary><strong>Security notes</strong></summary>

- TLS required for non-loopback binds
- Bearer token required (set `MCP_BEARER_TOKEN`)
- Helm chart includes optional Ingress and NetworkPolicy

</details>

---

## Goals

- **Zero changes to target app** — works via diagnostic IPC
- **Cross-platform** — Linux + Windows, containers first-class
- **Graceful NativeAOT** — unsupported tools return `not_supported`, not crashes
- **LLM-friendly** — summarized JSON, not raw `.nettrace`

---

## Build & Test

```bash
dotnet build
dotnet test
```

Requires .NET 10 SDK (pinned in `global.json`).

<details>
<summary><strong>Contributor setup (shared dev instance)</strong></summary>

```bash
scripts/local-mcp.sh start     # builds + starts in background
scripts/local-mcp.sh status
scripts/local-mcp.sh logs -f
scripts/local-mcp.sh stop
```

Add to `~/.copilot/mcp-config.json`:
```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": { "Authorization": "Bearer demo-local-token-2026" }
    }
  }
}
```

</details>

---

## Roadmap

| Phase | Status | Description |
|-------|--------|-------------|
| 1-3 | ✅ | Foundation + Core diagnostics + MCP server |
| 4 | ✅ | GC, exceptions, EventSources, dumps |
| 5 | ✅ | Kubernetes sidecar ([`deploy/k8s/`](./deploy/k8s)) |
| 6 | ✅ | Documentation polish |
| 7 | ✅ | Cloud integrations (Azure, AWS, GCP) |
| 8 | ✅ | Tool consolidation (24 → 15 tools) |
| **12** | ✅ | **Diagnostic Journey UX** — auto-hints + IoT triage |
| Next | ⏳ | Heap diff, GC overlay, NativeAOT publish |

---

## License

TBD.
