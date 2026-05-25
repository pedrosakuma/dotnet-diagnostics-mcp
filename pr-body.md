## Summary

RFC 0002 §7.3 #213 — final alias-removal wave. Deletes the 24 deprecated
`[DeprecatedTool]`-tagged MCP tools that were superseded by the 7 unified
discriminator tools introduced earlier in the RFC. The MCP tool surface is
now exactly **15 tools** (the ≤15 acceptance criterion in #213).

## Tools removed (24)

Process / info → `inspect_process`:
`list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`,
`get_container_signals`, `get_memory_trend`

Event collection → `collect_events`:
`snapshot_counters`, `collect_exceptions`, `collect_gc_events`,
`collect_event_source`, `collect_activities`

Sampling → `collect_sample`:
`collect_cpu_sample`, `collect_off_cpu_sample`, `collect_allocation_sample`

Heap → `inspect_heap`:
`inspect_dump`, `inspect_live_heap`

Snapshot drilldown → `query_snapshot`:
`query_heap_snapshot`, `query_thread_snapshot`, `query_off_cpu_snapshot`,
`query_collection`, `get_call_tree`

Orchestrator listing → `list_orchestrator`:
`list_pods`, `list_active_investigations`

Byte fetch → `get_bytes`:
`get_module_bytes`, `get_dump_bytes`

## Surviving surface (15)

`inspect_process`, `collect_events`, `collect_sample`, `inspect_heap`,
`query_snapshot`, `list_orchestrator`, `get_bytes`,
`collect_process_dump`, `collect_thread_snapshot`, `capture_method_bytes`,
`start_investigation`, `export_investigation_summary`, `compare_to_baseline`,
`attach_to_pod`, `detach_from_pod`.

## Changes

- Stripped 24 `[DeprecatedTool] + [McpServerTool]` attribute pairs across
  `DiagnosticTools.cs` (22) and `OrchestratorTools.cs` (2). The static
  methods themselves remain `public static` so unified tools keep
  dispatching to them.
- Deleted `src/DotnetDiagnosticsMcp.Server/Tools/Deprecation/` (DeprecatedToolAttribute,
  ToolDeprecationFilters, ToolDeprecationRegistry) and the matching
  `ToolDeprecationRegistryTests.cs`. Removed the DI filter wiring in
  `DiagnosticServiceRegistration.cs`.
- Updated `InvestigationProxyCallToolFilter.BypassToolNames` to the new
  set (`list_orchestrator`, `attach_to_pod`, `detach_from_pod`).
- `InvestigationPlanner` now emits unified tool names (`collect_events` with
  `kind=...`, `collect_sample` with `kind=...`, etc.) for every step in
  cold / warm / hypothesis plans.
- LLM-facing strings rewritten: `ServerInstructionsText`, `DiagnosticPrompts`,
  Resources (`HeapSnapshotResources`, `InvestigationGuideResources`,
  `TraceSessionResources`), unified-tool `NextActionHint` strings, and the
  `OrchestratorTools.DetachFromPod` hint (now points at `list_orchestrator`).
- Removed `using ...Deprecation;` from every consumer.
- Rewrote the legacy-name compatibility test suite in
  `tests/.../Compatibility/` (deleted) and 8 integration test files (call
  sites, allowlist InlineData, observability metric records, stdio
  smoke-test list-tools assertion, `BuildErrorText` labels, planner step-id
  expectations).
- Docs sweep (17 files in `docs/` + `AGENTS.md`, `README.md`, samples,
  `scripts/add-scopes.py`): every legacy tool reference replaced with the
  unified equivalent.
- Created `CHANGELOG.md` with the full breaking-change entry.

## Verification

- `dotnet build DotnetDiagnosticsMcp.slnx -c Release` — 0 errors.
- `dotnet test DotnetDiagnosticsMcp.slnx -c Release --no-build` —
  341/341 IntegrationTests pass; 312/312 Core.Tests pass (3 R2R-resolver
  tests skipped in this environment, as on `main`).
- `tools/list` against the integration factory returns exactly the 12
  non-orchestrator tools; orchestrator factories add 3 more for a total
  of 15. Asserted by `McpToolsTests.ListTools_ExposesEveryCoreToolWithSchema`
  using `BeEquivalentTo` (tightened from `Contain`).
- `grep -rn "DeprecatedTool" src/ tests/` returns 0 results.

Closes #213.
