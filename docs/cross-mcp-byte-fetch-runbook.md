# Cross-MCP byte fetch in orchestrator mode

Use this workflow when `dotnet-diagnostics-mcp` is attached to a remote pod through the orchestrator proxy but the sibling MCPs (`dotnet-assembly-mcp`, `dotnet-native-mcp`) are running somewhere else and cannot see the pod-local filesystem.

## Prefer twin sidecars when possible

The default topology is still **twin sidecars**: run the sibling MCPs next to the diagnostics sidecar so handoff paths already exist on the same host.

Use byte fetch only when that is not feasible:

- the sibling MCPs must stay on the client host,
- the cluster does not allow extra ephemeral containers, or
- you need a one-off binary / dump handoff without changing the pod topology.

## Worked example

1. The LLM sees a hotspot carrying `MethodIdentity { moduleVersionId, metadataToken }`.
2. It calls `get_bytes(kind="module")(moduleVersionId="…", asset="pe", processId=12345)` and keeps following the returned `NextActionHint` until `nextOffset == null`.
3. It reassembles the chunks into a local scratch file and verifies the envelope's full-artifact `sha256`.
4. If the PE envelope reported `companionPdbPath` or `pdbIsEmbedded=true`, it repeats the same loop with `asset="pdb"`.
5. It then calls `dotnet-assembly-mcp.get_method` (or `load_assembly` + `get_method`) with the local materialized path.

The same pattern works for dumps: `collect_process_dump(confirm=true)` → `get_bytes(kind="dump")(dumpFilePath, offset, maxBytes)` → materialize locally → hand off to the dump-aware sibling MCP.

## Bandwidth / chunking guidance

- Default chunk size is **4 MiB**.
- The server caps each response at **16 MiB**.
- Artifacts over **256 MiB** are rejected instead of partially streamed.
- The envelope's `sha256` is for the **full artifact**, so the client can deduplicate repeated fetches and verify reassembly without an extra round-trip.

## Security expectations

- Both `get_bytes(kind="module")` and `get_bytes(kind="dump")` require the literal scope **`module-bytes-read`**.
- This scope is **literal**: a root / `*` token does **not** auto-grant it.
- Every call is audit-logged with `tokenName`, `tool`, the streamed identifier (`mvid` or `dumpPath`), `offset`, `chunkSize`, and `totalSize`.
- The materialized local path is still an **untrusted hint**. Sibling MCPs must validate it per the handoff contract before opening it.

## Reference

See [`docs/central-orchestrator-design.md` §4.5](./central-orchestrator-design.md#45-cross-mcp-handoff-in-orchestrator-mode).
