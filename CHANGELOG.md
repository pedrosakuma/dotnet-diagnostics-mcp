# Changelog

## [Unreleased]

### Breaking
- **RFC 0002 §7.3 #213** — Removed 24 deprecated MCP tools that were superseded by 7 unified discriminator tools. The 15-tool consolidated surface is now the only entry point.
  - Removed: `list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`, `get_container_signals`, `get_memory_trend`, `snapshot_counters`, `collect_cpu_sample`, `collect_allocation_sample`, `get_call_tree`, `collect_off_cpu_sample`, `query_off_cpu_snapshot`, `query_collection`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, `collect_event_source`, `inspect_dump`, `inspect_live_heap`, `query_heap_snapshot`, `query_thread_snapshot`, `list_pods`, `list_active_investigations`, `get_module_bytes`, `get_dump_bytes` (24 tools).
  - Use the corresponding unified tool with the appropriate `kind`/`view`/`source` discriminator (see `docs/tool-reference.md`).
