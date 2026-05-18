# AGENTS.md

> Operating guide for AI coding agents (Copilot CLI, Codex, Claude Code, Cursor, etc.) working on this repository. Humans benefit too.

## What this project is

`dotnet-diagnostics-mcp` is an **MCP server** that lets an LLM perform on-demand performance diagnostics on running **.NET 10** applications — locally or in a Kubernetes sidecar — **without any modification to the target app**.

The server attaches to the .NET runtime diagnostic IPC socket and exposes 9 tools (process discovery, capability detection, EventCounters snapshot, CPU sampling, exception collection, GC events, EventSource passthrough, process dump) over Streamable HTTP with bearer-token auth.

**Status:** MVP complete (Phases 1–6). Active work on Phase 7 is tracked in [issue #17](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17) and the milestone [`Phase 7 — Roadmap`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/milestone/1).

## Repository layout

```
src/
  DotnetDiagnosticsMcp.Core/      — IPC + EventPipe primitives, no MCP knowledge
  DotnetDiagnosticsMcp.Server/    — MCP tools, HTTP transport, bearer auth
tests/
  DotnetDiagnosticsMcp.Core.Tests/              — live process tests (spawn sample, attach, assert)
  DotnetDiagnosticsMcp.Server.IntegrationTests/ — HTTP + MCP protocol tests
samples/
  CoreClrSample/      — minimal ASP.NET API used by Core tests
  NativeAotSample/    — used to validate capability detection
  BadCodeSample/      — 7 anti-pattern endpoints to exercise the LLM diagnostic loop
deploy/
  Dockerfile          — multi-stage publish; copies .editorconfig for analyzers
  k8s/sample-sidecar.yaml — reference K8s topology
docs/
  README.md, tool-reference.md, investigation-playbooks.md,
  bad-code-scenarios.md, local-docker-sidecar.md, client-setup.md
```

## Build, test, run

All commands are run from the repo root.

```bash
# Build everything
dotnet build DotnetDiagnosticsMcp.slnx -c Release

# Run all tests (Core tests spawn a live sample process; require .NET 10 SDK on PATH)
dotnet test DotnetDiagnosticsMcp.slnx -c Release --no-build

# Run only the integration tests
dotnet test tests/DotnetDiagnosticsMcp.Server.IntegrationTests/ -c Release --no-build

# Run the MCP server locally
dotnet run --project src/DotnetDiagnosticsMcp.Server -c Release
# Server listens on http://127.0.0.1:5050 — bearer token from appsettings (default "dev-token")
```

### Local Docker sidecar (matches K8s topology)

See [`docs/local-docker-sidecar.md`](./docs/local-docker-sidecar.md) for the validated walkthrough. Short version:

```bash
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
docker build -t badcode-sample:dev -f samples/BadCodeSample/Dockerfile samples/BadCodeSample
docker network create dbgmcp-net
docker volume create badcode-tmp
docker run -d --name badcode --network dbgmcp-net -v badcode-tmp:/tmp -p 127.0.0.1:18180:8080 badcode-sample:dev
docker run -d --name badcode-mcp --network dbgmcp-net \
  --pid=container:badcode -v badcode-tmp:/tmp --user 0 \
  -p 127.0.0.1:18887:5050 dotnet-diagnostics-mcp:dev
```

## Critical conventions you must respect

These are the easy-to-break things that have cost us debugging time before.

### 🔌 Diagnostic socket UID — same UID, every time

The .NET diagnostic IPC socket at `/tmp/dotnet-diagnostic-<pid>` inherits the **target app's UID**. The MCP sidecar must run with **the same UID** or it gets `ServerNotAvailableException: Permission denied`.

- **Local dev**: `docker run --user 0 …` on the sidecar (target runs as root).
- **K8s**: pod-level `securityContext` with matching `runAsUser` + `runAsGroup` + `fsGroup`. See [`deploy/k8s/sample-sidecar.yaml`](./deploy/k8s/sample-sidecar.yaml).

### 🐳 `.dockerignore` must re-include `.editorconfig`

Our `.dockerignore` starts with `*` (deny-all) and re-includes specific paths. **If you remove the `!.editorconfig` line, the publish breaks** with CA1848/CA1873 errors because the analyzer suppressions live in `.editorconfig`. Same applies to `!samples/` for sample images.

### ⏱️ EventPipe session startup is not instant

EventPipe sessions take ~500ms–1s to fully start. Then `EventCounters` payloads arrive at `EventCounterIntervalSec` boundaries.

- Always give live tests **≥6s** of `duration` for counters with `intervalSeconds=1`.
- For exception/GC collection, **start the session BEFORE** the load that generates events.

```bash
# WRONG — exceptions collector starts after curl loop finishes
curl … & sleep 0; dotnet … collect_exceptions
# RIGHT — schedule load to happen during the collection window
( sleep 2; curl … ) &
dotnet … collect_exceptions  # synchronous
```

### 🧪 Live tests are real

`tests/DotnetDiagnosticsMcp.Core.Tests/LiveCoreClrProcessTests.cs` spawns the `CoreClrSample` webapi via `dotnet run --no-build` and attaches to it. Required: .NET 10 SDK on `PATH`, ability to bind to `127.0.0.1:0`, and ~10s of runtime. CI runs both Linux and Windows runners.

### 🎯 One MCP tool per concept (≤10 tools per context)

Anthropic recommends ≤10 tools per LLM context. We currently have 9. **Don't add tools speculatively**. New capabilities should either:

1. Extend an existing tool with a parameter, or
2. Be exposed as a Resource (`audience=["assistant"]`) or Prompt, not a Tool, or
3. Be split with `mcp-drilldown` so a single "summary" tool drives optional "detail" tools through a `handle`.

See Phase 7 issue [#8 `mcp-drilldown`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/8).

## Phase 7 — what is being designed and why

All open work has a GitHub issue. Read the **meta tracking issue first**: [#17 Phase 7 — Post-MVP Roadmap](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17).

The dependency graph and execution order are documented there. Two deep-research artifacts back the design — they live in the session workspace (not in the repo), but every issue inlines the relevant findings. Topics:

- **Spec catch-up** (`spec-compliance` label) — bump to MCP `2025-11-25`, `outputSchema`, Tasks, elicitation with graceful degradation.
- **Discoverability** (`discoverability` label) — `serverInfo.description`, tool titles, hints, structured errors, Prompts.
- **Drill-down** (`drilldown` label) — handle + summary + detail tools, `start_investigation` meta-tool (cold/warm/hypothesis), portable investigation summaries with provenance and lineage.
- **Diagnostics** — source-level resolution (TraceLog + SourceLink), ClrMD dump inspection, NativeAOT sampling fallback.
- **Infra** — NativeAOT publishing, central K8s topology, cloud platforms.

## How to contribute as an agent

When picking up an issue:

1. **Read the meta tracking issue** ([#17](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/17)) for the dependency graph. Don't start blocked work.
2. **Read the linked research findings** in the issue body before designing.
3. **Build + run tests** before and after — `dotnet build` and `dotnet test` from the repo root.
4. **Don't add tools without an issue** — discuss first in the relevant `drilldown` or `discoverability` issue.
5. **Keep PRs small and reference the issue** (`Closes #N`).
6. **Don't commit** secrets, dumps (`*.dmp`), or `.nettrace` files.

## Things deliberately not in scope

- **Modifying the target application** — non-goal. Everything must work over the diagnostic socket.
- **Persistent server-side state** — server stays stateless; investigation memory is portable JSON the agent persists externally (see issue [#10](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/10)).
- **A web UI** — this is an MCP server; the UI is whatever MCP client the human uses.
- **Replacing dotnet-monitor** — different goals. `dotnet-monitor` is rule-based collection for ops; we are interactive diagnosis for an LLM. They complement each other.
