# Changelog

## [Unreleased]

### Added
- **#233** — Azure App Service + Container Apps discovery backends for the `discover_azure` MCP tool (parent #230). Replaces the `NotImplementedException` stubs from #232 with real implementations backed by `Azure.ResourceManager.AppService` / `Azure.ResourceManager.AppContainers`. Backends consume thin adapter seams (`IAzureWebSiteCollectionAdapter`, `IAzureContainerAppCollectionAdapter`) so they can be unit-tested with in-memory fakes. Honors `resourceGroup`, `includeStopped`, `cursor` (opaque Azure SDK continuation token pass-through), and emits readiness warnings (`Windows OS — sidecar not supported`, `No second container detected — sidecar topology not deployed`, `Scale=0`). Function apps are excluded from `kind=webapps` results. AKS still routes through the stub until #234. Requires `Reader` on the subscription for both kinds. See `docs/azure-discovery.md` for the full warnings catalog.

### Breaking
- **RFC 0002 §7.3 #213** — Removed 24 deprecated MCP tools that were superseded by 7 unified discriminator tools. The 15-tool consolidated surface is now the only entry point.
  - Removed: `list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`, `get_container_signals`, `get_memory_trend`, `snapshot_counters`, `collect_cpu_sample`, `collect_allocation_sample`, `get_call_tree`, `collect_off_cpu_sample`, `query_off_cpu_snapshot`, `query_collection`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, `collect_event_source`, `inspect_dump`, `inspect_live_heap`, `query_heap_snapshot`, `query_thread_snapshot`, `list_pods`, `list_active_investigations`, `get_module_bytes`, `get_dump_bytes` (24 tools).
  - Use the corresponding unified tool with the appropriate `kind`/`view`/`source` discriminator (see `docs/tool-reference.md`).
