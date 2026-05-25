# dotnet-diagnostics-mcp

[![CI](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml)

> **Status:** MCP server functional with 21 diagnostic tools, two transports
> (HTTP + stdio), and 6 curated investigation prompts. End-to-end tests pass
> on Linux + Windows. See [`docs/`](./docs) for the tool reference and
> investigation playbooks.

An **MCP server** that lets an LLM perform on-demand performance diagnostics on running
**.NET 10** applications — locally or in Kubernetes — **without any modification to the
target app**.

It builds on the existing .NET diagnostics pipeline (`Microsoft.Diagnostics.NETCore.Client`
+ EventPipe + ClrMD) and exposes it as MCP tools over either **Streamable HTTP** (sidecar /
shared dev instance) or **stdio** (MCP client spawns the server as a subprocess), so any
MCP-aware client (Claude Desktop, Copilot CLI, custom agents, ...) can drive an investigation.

## Goals

- **Zero changes to the target app** for CoreCLR (JIT/R2R) processes
- **Cross-platform** Linux + Windows (containers as a first-class citizen)
- **Graceful NativeAOT support** — tools that can't work on AOT (CPU sampling, gcdump)
  return a clear `not_supported` instead of crashing
- **LLM-friendly outputs** — every tool returns summarized JSON (top-N hotspots, aggregates),
  not raw `.nettrace` blobs

## Repository layout

```
src/
  DotnetDiagnosticsMcp.Core/        # diagnostic primitives (process discovery, EventPipe sessions, parsing)
  DotnetDiagnosticsMcp.Server/      # ASP.NET Core host + MCP tools + bearer auth
samples/
  CoreClrSample/            # plain ASP.NET Core webapi used as a target for testing
  NativeAotSample/          # webapi with PublishAot=true and EventSourceSupport=true
tests/
  DotnetDiagnosticsMcp.Core.Tests/
  DotnetDiagnosticsMcp.Server.IntegrationTests/
```

## Tools at a glance

| Tool | Purpose |
|---|---|
| `inspect_process(view="list")` / `inspect_process(view="info")` | Discover .NET processes via diagnostic IPC |
| `inspect_process(view="capabilities")` | Detect CoreCLR vs NativeAOT and what's usable |
| `inspect_process(view="container")` | Linux cgroup v2: CPU throttling, memory pressure, OOM kills, PSI |
| `collect_events(kind="counters")` | EventCounters over a window (cpu, memory, requests, ...) |
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

Full schemas and return shapes: [`docs/tool-reference.md`](./docs/tool-reference.md).
Common investigation recipes: [`docs/investigation-playbooks.md`](./docs/investigation-playbooks.md).
NativeAOT coverage matrix (tool × runtime × OS): [`docs/aot-coverage.md`](./docs/aot-coverage.md).
Client setup (C# SDK, GUI clients, curl): [`docs/client-setup.md`](./docs/client-setup.md).
Kubernetes sidecar: [`deploy/k8s/README.md`](./deploy/k8s/README.md).
Central Kubernetes orchestrator (Helm chart): [`deploy/helm/README.md`](./deploy/helm/README.md).

> **TLS is required for any non-loopback bind.** The MCP server authenticates
> with a static bearer token in the `Authorization` header. The HTTP server
> refuses to start when bound to a non-loopback address (anything other than
> `127.0.0.1` / `::1` / `localhost`) without an operator-supplied
> `MCP_BEARER_TOKEN`. Terminate TLS at an Ingress / Gateway / mesh sidecar in
> front of the Service — never expose the orchestrator on plain HTTP outside
> loopback. The Helm chart ships an optional `ingress.yaml` (gated by
> `ingress.enabled`) and an optional `networkpolicy.yaml` for L3/L4
> fail-closed ingress (gated by `networkPolicy.enabled`).
Azure (App Service + Container Apps) recipes: [`deploy/azure/README.md`](./deploy/azure/README.md).
AWS (ECS / Fargate) recipe: [`deploy/aws/README.md`](./deploy/aws/README.md).
GCP (Cloud Run) recipe: [`deploy/gcp/README.md`](./deploy/gcp/README.md).

> **`processId` is optional everywhere.** When the sidecar only sees one .NET
> process the server auto-resolves it and stamps a capability digest on every
> response (`resolvedProcess.autoResolved = true`), so the
> `inspect_process(view="list")` → `inspect_process(view="capabilities")` opener can be skipped
> entirely. On ambiguity / nothing visible you get a structured error with the
> candidate list inline. See [issue #42](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/42).

> **Prompts (curated playbooks).** The server also exposes 6 MCP Prompts that
> pre-package the recipes in [`docs/investigation-playbooks.md`](./docs/investigation-playbooks.md):
> `diagnose-high-latency`, `diagnose-memory-growth`, `diagnose-5xx-errors`,
> `diagnose-slow-outbound-http`, `triage-nativeaot`, `diagnose-safely-in-prod`.
> Every input is optional, every body is annotated for the LLM (`audience: ["assistant"]`).
> See [issue #44](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/44) and the
> [Prompts section in `docs/tool-reference.md`](./docs/tool-reference.md#prompts-curated-playbooks).

## Install (consumers)

Three distributions of the MCP server, all wire-compatible. Pick whichever fits your environment best — see [`docs/consumer-install.md`](./docs/consumer-install.md) for the full walkthrough including supervisor templates (systemd, Windows Scheduled Task, launchd) and `mcp-config.json` snippets.

```bash
# .NET global tool (requires .NET 10 SDK)
dotnet tool install -g dotnet-diagnostics-mcp
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787

# Container (no SDK needed; predictable filesystem). The default image bundles `perf` so
# off-CPU sampling and NativeAOT CPU sampling work out of the box (still need
# CAP_PERFMON, or perf_event_paranoid <= 1 on the host, for `perf` to actually collect).
docker run -d --restart unless-stopped -p 127.0.0.1:8787:8080 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest

# Want a smaller image without `perf` (~80 MB lighter, disables off-CPU sampling
# and the perf-replay thread-snapshot fallback)? Use the `-lean` tag:
#   ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest-lean

# Or grab a self-contained single-file binary for your OS/arch from the Releases page.
```

### Transports — pick by deployment shape

- **stdio (recommended for local dev with Copilot CLI / Claude Desktop / Cursor):** the
  MCP client spawns `dotnet-diagnostics-mcp --stdio` as a subprocess and talks JSON-RPC
  over stdio. The client owns the process lifecycle, so `dotnet tool update -g …` is
  picked up automatically on the next client reload — no stale daemons.
- **HTTP (sidecar in K8s, shared dev host, multi-client):** long-lived daemon on
  `127.0.0.1:8787` (or a sidecar container). Use `MCP_BEARER_TOKEN` + optionally
  `DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART=true` so the built-in `StaleBinaryWatcher` asks
  the supervisor (systemd / `docker --restart=always` / K8s) to recycle the process
  when the on-disk image MVID drifts. See [`docs/client-setup.md`](./docs/client-setup.md)
  for the full `mcp-config.json` snippets per client.

> 🐧 **Linux:** on Debian/Ubuntu/WSL/Codespaces the `kernel.yama.ptrace_scope=1` default blocks the four ClrMD-backed tools (`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` against a live PID, `collect_process_dump`). See [Consumer install → § 1.5 Linux: enabling ClrMD-backed tools (ptrace)](./docs/consumer-install.md#15-linux-enabling-clrmd-backed-tools-ptrace) for the one-line fix per distribution. EventPipe-only tools (counters, CPU sample, exceptions, GC, ActivitySource spans, EventSources) work out of the box.

### Verifying releases

Every release artifact — NuGet package, self-contained binary archive, and GHCR container image — is published with a **SLSA build provenance attestation** generated by [`actions/attest-build-provenance`](https://github.com/actions/attest-build-provenance) and signed by Sigstore via GitHub's OIDC issuer. The attestation proves the artifact was built by this repository on a specific commit by GitHub-hosted runners — no separate cert to install, no key to rotate.

Verify with the GitHub CLI:

```bash
# NuGet package
gh attestation verify dotnet-diagnostics-mcp.<version>.nupkg \
  --repo pedrosakuma/dotnet-diagnostics-mcp

# Self-contained binary archive (.tar.gz or .zip)
gh attestation verify dotnet-diagnostics-mcp-<version>-linux-x64.tar.gz \
  --repo pedrosakuma/dotnet-diagnostics-mcp

# Container image — attestation is co-located in the registry next to the image,
# and the OCI manifest digest (the multi-arch index) is the attested subject.
gh attestation verify oci://ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:<version> \
  --repo pedrosakuma/dotnet-diagnostics-mcp
```

A passing verification confirms the artifact was built by `pedrosakuma/dotnet-diagnostics-mcp` on the expected commit and tag.

### Joint with `dotnet-assembly-mcp` (recommended for handoff resolution)

For the full handoff story — `MethodIdentity` / `TypeIdentity` resolving to
decompiled bodies, call graphs, attributes, type hierarchy — run both servers
together with the joint compose file:

```bash
export ASSEMBLIES_DIR=/abs/path/to/your/published/binaries
docker compose -f deploy/docker-compose.yml up -d
# diagnostics: http://localhost:8787/mcp
# assembly:    http://localhost:8788/mcp
```

The same `docker-compose.yml` ships in
[`pedrosakuma/dotnet-assembly-mcp:deploy/docker-compose.yml`](https://github.com/pedrosakuma/dotnet-assembly-mcp/blob/main/deploy/docker-compose.yml)
— bring it up from either checkout. Set `MCP_BEARER_TOKEN` on the host to gate
both servers with one shared token.

Health probe used by every supervisor (and the container's `HEALTHCHECK`):

```bash
dotnet-diagnostics-mcp --health-check --urls http://127.0.0.1:8787
```

The companion [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) is **optional in a dev environment** after #28 — `MethodIdentity` payloads now carry a resolved `SourceLocation` whenever a PDB is reachable on the diagnostics box, so the LLM can open `File:Line` directly. The partner is the right call for stripped binaries, decompilation, and call-graph queries.

## Build & test

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK (pinned in `global.json`).

## Authentication

The HTTP transport requires a Bearer token on every request to `/mcp/*` (the `/health` endpoint is open). The stdio transport does **not** use a token — the spawning client is already trusted at the OS level.

- Set `MCP_BEARER_TOKEN` to use a fixed token (recommended).
- If unset, an ephemeral token is generated at startup and printed in the logs.

```bash
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/DotnetDiagnosticsMcp.Server
```

### Contributor setup (shared dev instance)

> Looking for a consumer install? See the [Install](#install-consumers) section above.

The HTTP transport is multi-client, so a single shared MCP instance on `127.0.0.1:8787` serves any number of concurrent clients (multiple `gh copilot` sessions, editors, etc). Use the lifecycle wrapper for an idempotent, deterministic local setup against the current source checkout:

```bash
scripts/local-mcp.sh start     # builds (Release) + starts in background; idempotent
scripts/local-mcp.sh status
scripts/local-mcp.sh logs -f
scripts/local-mcp.sh restart
scripts/local-mcp.sh stop
```

The script pins URL (`LOCAL_MCP_URL`, default `http://127.0.0.1:8787`) and bearer
token (`MCP_BEARER_TOKEN`, default `demo-local-token-2026`), uses
`--no-launch-profile` so `launchSettings.json` can't silently override the port,
and stores its PID at `/tmp/dotnet-diagnostics-mcp.pid` and logs at
`/tmp/dotnet-diagnostics-mcp.log` (overridable via `LOCAL_MCP_PIDFILE` /
`LOCAL_MCP_LOGFILE`). Pair with the matching entry in `~/.copilot/mcp-config.json`:

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

There is also a dedicated `local-mcp` launch profile for one-off foreground runs:
`dotnet run --project src/DotnetDiagnosticsMcp.Server --launch-profile local-mcp`.

## Roadmap

See [`docs/`](./docs) (to be filled in) and the planning notes in the session workspace.
Phases:

1. ✅ Foundation
2. ✅ Core diagnostics (process discovery, capability detection, counters, CPU sampling)
3. ✅ MCP server MVP wired to Core
4. ✅ Advanced tools (GC, exceptions, custom EventSources, dumps)
5. ✅ Kubernetes sidecar topology + manifests (see [`deploy/k8s/`](./deploy/k8s))
6. ✅ Documentation polish (tool reference, investigation playbooks, client setup)
7. ✅ Cloud-native integrations: Azure App Service + Container Apps (see [`deploy/azure/`](./deploy/azure)), AWS ECS / Fargate (see [`deploy/aws/`](./deploy/aws)), and GCP Cloud Run (see [`deploy/gcp/`](./deploy/gcp)).
8. ⏳ Future: NativeAOT publish.

## License

TBD.
