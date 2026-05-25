# Changelog

## [Unreleased]

### Added
- `collect_events(kind="counters")` now subscribes to `System.Diagnostics.Metrics`
  meters via the new `meters` / `maxInstrumentTimeSeries` parameters, surfaces
  Meter time series and histogram percentiles in `CounterSnapshot`, and carries
  cap/error notes when Meter cardinality is truncated.

### Fixed
- `deploy/Dockerfile`: removed dev-only `"Urls": "http://127.0.0.1:8787"` from
  shipped `appsettings.json` so `ASPNETCORE_URLS=http://0.0.0.0:8080` (set in
  the image) is no longer overridden. The `docs/local-docker-sidecar.md`
  quickstart now works out-of-the-box without `-e Urls=http://0.0.0.0:8080`.
  Local dev launch is unaffected — `launchSettings.json` profiles still set
  `applicationUrl` explicitly.
- `inspect_process(view=list)` and the ClrMD `PermissionDenied` envelope no
  longer emit `nextTool="get_diagnostic_capabilities"` (removed in RFC 0002
  §7.3); they now correctly point at `inspect_process(view="capabilities")`.

## [0.4.0] — 2026-05-25

Highlights: RFC 0002 tool surface consolidation (24 legacy tools → 15
unified discriminator tools, breaking), central K8s orchestrator
(`attach_to_pod` + server-side proxy), Azure discovery
(`discover_azure` for App Service / Container Apps / AKS), AWS ECS &
GCP Cloud Run sidecar recipes, comprehensive security hardening
(OIDC/JWT, per-tool RBAC scopes, supply-chain), and SLSA build
provenance attestations on every release artifact.

GitHub's auto-generated release notes list every PR; the entries below
group the work by theme.

### Breaking
- **RFC 0002 §7.3 (#213)** — Removed 24 deprecated MCP tools that were superseded by 7 unified discriminator tools. The 15-tool consolidated surface is now the only entry point.
  - Removed: `list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`, `get_container_signals`, `get_memory_trend`, `snapshot_counters`, `collect_cpu_sample`, `collect_allocation_sample`, `get_call_tree`, `collect_off_cpu_sample`, `query_off_cpu_snapshot`, `query_collection`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, `collect_event_source`, `inspect_dump`, `inspect_live_heap`, `query_heap_snapshot`, `query_thread_snapshot`, `list_pods`, `list_active_investigations`, `get_module_bytes`, `get_dump_bytes`.
  - Use the corresponding unified tool with the appropriate `kind`/`view`/`source` discriminator (see `docs/tool-reference.md`).
- **RFC 0002 Stage B (#211)** — Removed `runAsJob` flag and retired `get_collection_status` / `cancel_collection` in favor of MCP-native progress + cancellation (#222).
- **Container image (#111)** — `perf` now ships by default; the GHCR tag suffix is inverted to `-lean` for the perf-less variant.

### Added — RFC 0002 unified tool surface
- `inspect_process(view=...)` bootstrap consolidation (#209/#218).
- `collect_events(kind=...)` unified EventPipe collectors (#208/#215).
- `collect_sample(kind=cpu|off_cpu|allocation)` unified sample collectors (#210/#221).
- `query_snapshot(handle, view, ...)` unified drilldown verbs (#207/#223).
- `inspect_heap(source=live|dump)` merged heap inspectors (#206/#219).
- `list_orchestrator(kind=pods|investigations)` (#212/#217).
- `get_bytes(kind=module|dump)` merged byte-fetch tools (#205/#216).
- MCP-native progress + cancellation for long-running collectors (#222).
- Shared compatibility scaffolding (#204/#214) and `PodLocalToolSurfaces` single source of truth for tool registration (#220).

### Added — Central K8s orchestrator (#20)
- `list_pods` + Kubernetes client scaffolding (P3a, #143).
- `attach_to_pod` + investigation handles (P3b-1, #146).
- Port-forward proxy at `/proxy/{handle}` (P3b-2, #150).
- Investigation session binding (P3b-3a, #152).
- Server-side proxy intercept for bound MCP sessions (#154).
- `detach_from_pod` + `list_active_investigations` + TTL reaper (P4, #155).
- Orchestrator deployment assets (#156) and kind integration test for attach + proxy round-trip (#157).
- Observability — metrics, audit, OpenTelemetry (#198/#201).
- Session-aware target resolution foundation (#142).

### Added — Cloud discovery & deployment
- **Azure discovery (parent #230)**: `discover_azure` tool contract (#232/#236), ARM client foundation (#231/#235), App Service + Container Apps backends (#233/#237), AKS cluster listing + process-local kubeconfig handle subsystem (#234/#238).
- **AWS** ECS/Fargate sidecar recipe (#22 Phase 1, #141).
- **GCP** Cloud Run sidecar recipe (#22, #161).
- Cross-MCP byte fetch tools (#195).

### Added — Security hardening
- OIDC / JWT auth on MCP HTTP transport (#196/#200).
- B5 per-tool authorization scopes: `BearerTokenRegistry` + scoped middleware (#182/#188), `[RequireScope]` on every `[McpServerTool]` (#183/#190), Helm chart scoped-token wiring (#186/#191), per-call `confirm=true` for `collect_process_dump` (#187/#192), subsumption of orchestrator + diagnostics admin flags into scopes (#193/#185/#194).
- B4 gating: heap secret leakage, event source allowlist, symbol-server SSRF (#165/#179).
- Sandboxed dump and JIT-bytes output paths (#163/#171).
- Hardened default Helm/RBAC/TLS posture (#162/#172).
- Cross-MCP handoff path hints treated as untrusted (#168/#169).
- Supply-chain hardening (#167/#170) and SLSA build provenance attestations on every release artifact (#149/#159).
- Hardened investigation proxy (#164/#180).

### Added — Diagnostics surface
- `collect_activities` tool (#113/#129).
- Thread snapshot deadlock view (#115/#131), threadpool view (#118/#136), async view (#117), unique thread snapshot groups (#130).
- Heap object drilldowns for snapshot queries (#133).
- Heap async view (#117).
- Opt-in closed-generic enrichment in `collect_cpu_sample` (#86/#127).
- Project `MethodIdentity` into allocation call trees (#100/#126).
- Uniform external symbol resolution (#112/#124).
- Accept `SeSystemProfilePrivilege` for off-CPU sampling on Windows (#89/#59/#128).
- Uniform `depth` parameter + managed↔kernel off-CPU stack merge (#41 slice 2c, #82).
- Extended kernel capability matrix (#41/#132).
- Experimental MCP Tasks support for long-running collects (#135).

### Fixed
- NativeAOT Linux `collect_thread_snapshot` (carried from v0.3.1 — eu-stack partial-success handling).
- Process discovery filters stale diagnostic sockets and Linux TID collisions (#110).
- `PerfScriptParser` PID filter cross-OS (#122).
- `collect_cpu_sample` `runAsJob` depth propagation (#121/#123).
- Windows integration test hangs (#120/#125).
- Kind-integration cluster-wide RBAC (#178).
- CI flake mitigations: serialize test assemblies + collect crash dumps (#148), tolerate documented ubuntu host-crash flake via retry wrapper (#189), conditional quarantine for CpuSampler closed-generic flakes (#145/#147/#160), drive allocation pressure in Kind_Allocation compat test (#225), serialize legacy admin-bypass latch tests (#202/#228).

### Docs
- Central K8s topology design (#15/#137), cloud integrations design (#16/#138), central MCP orchestrator design (Phase 1 spike, #139), AWS ECS + GCP Cloud Run recipes design (#140).
- `gh`/`git` shell-escape pitfalls (#119).
- AOT coverage matrix rewrite with OS columns (#97/#109).
- Agent workflow conventions in `AGENTS.md` (#229).

### CI / Chore
- Bumped GitHub Actions to Node 24-based majors (#151/#158).
