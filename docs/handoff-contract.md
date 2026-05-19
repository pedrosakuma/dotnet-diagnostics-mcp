# Handoff Contract: `MethodIdentity` (dotnet-diagnostics-mcp ⇄ dotnet-assembly-mcp)

> Status: stable since `dotnet-diagnostics-mcp` shipped #18. Companion MCP:
> [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp).

## Why a contract

`dotnet-diagnostics-mcp` observes **running** processes and reports hotspots, exceptions,
GC events, etc. — but it deliberately does **not** read application code. When the LLM
wants to inspect *the actual implementation* of a hot method (decompile, find callers,
walk IL), the natural next step is to hand off to `dotnet-assembly-mcp`, which operates
on the **on-disk** assemblies via `System.Reflection.Metadata`.

For that hop to work without speculative searches, both servers must agree on a single,
machine-readable identifier that round-trips to **one** `MethodDefinition` regardless of
generics, name mangling, or compiler-synthesized closure names.

## The canonical pair

```
(moduleVersionId, metadataToken)
```

- **`moduleVersionId`** — the PE module's MVID (`MetadataReader.GetModuleDefinition().Mvid`).
  Stable across copies of the same binary; distinct across rebuilds.
- **`metadataToken`** — the `MethodDef` token (table `0x06`), the integer key the assembly
  MCP uses verbatim.

Together they uniquely resolve a method without name parsing.

## `MethodIdentity` (full payload)

`dotnet-diagnostics-mcp` emits this record on every ranked hotspot of `collect_cpu_sample`
(via the `MethodIdentities` map on `CpuSampleTraceArtifact`, surfaced in the markdown /
JSON summary as `HotspotSummary.Identity`):

| Field             | Type      | Required for handoff | Description                                                              |
|-------------------|-----------|----------------------|--------------------------------------------------------------------------|
| `ModuleVersionId` | `Guid?`   | ✅ (with token)      | PE module MVID. `null` when the sidecar can't read the assembly off disk |
| `MetadataToken`   | `int?`    | ✅ (with mvid)       | IL `MethodDef` token. `null`/`0` for native-only frames                  |
| `ModuleName`      | `string?` | display              | Simple file name (e.g. `MyApp.dll`)                                      |
| `ModulePath`      | `string?` | helper               | Absolute path on disk when known                                         |
| `TypeFullName`    | `string?` | display              | Declaring type FQN (`Namespace.Outer+Inner`)                             |
| `MethodName`      | `string`  | display              | Bare method name (no signature)                                          |
| `GenericArity`    | `int`     | sanity-check         | Number of generic method parameters; `0` for non-generic methods         |

The `(mvid, token)` pair is the only field required by the consumer. Everything else is a
sanity-check label so a human (or the LLM) can confirm "this is the right method" without
loading the assembly first.

## Producer responsibilities (`dotnet-diagnostics-mcp`)

1. During the event walk, capture `TraceCodeAddress` per method-frame key.
2. For each top-N hotspot, extract `TraceMethod` from the `TraceCodeAddress` and read:
   - `TraceMethod.MethodToken` → `MetadataToken`
   - `TraceMethod.MethodModuleFile.FilePath` → `ModulePath`
   - `TraceMethod.FullMethodName` → split on the last `.` for `TypeFullName` / `MethodName`
3. Read the MVID directly from the PE on disk via
   `System.Reflection.Metadata.PEReader` + `MetadataReader.GetModuleDefinition().Mvid`
   (see `MvidReader`). **Do not** rely on `TraceModuleFile.PdbSignature` — it equals the
   MVID for portable PDBs but not for legacy Windows PDBs.
4. MVID reads are cached by absolute path for the lifetime of the sampler instance.
5. Emit identities **unconditionally** (no opt-in flag). They are cheap (one cached PE
   open per distinct hot module) and they are the bridge for every downstream
   investigation.
6. When a frame yields nothing useful (no token, no path, no name — e.g. a pure native
   frame), omit it from `MethodIdentities` rather than emitting an all-null record.

## Consumer responsibilities (`dotnet-assembly-mcp`)

1. Treat `(moduleVersionId, metadataToken)` as the **only** authoritative inputs to
   `get_method`, `decompile_method`, `scan_method_il`, `get_method_il`, and
   `find_callers`. `typeFullName` / `methodName` / `genericArity` are *display-only*
   sanity-check labels.
2. Resolve the assembly path independently (e.g. from a configured assembly probe path,
   or by accepting `modulePath` as a hint). The handoff does **not** transmit bytes —
   both servers must have access to the same binary on disk (or the same content
   addressable cache).
3. Surface a clear error if no loaded module matches the MVID — the LLM can then load the
   assembly explicitly via `load_assembly(path)` using the producer-supplied `modulePath`
   as a hint.

## Worked example

After `collect_cpu_sample` returns, a top hotspot might look like (truncated):

```jsonc
{
  "symbol": {
    "module": "CoreClrSample.dll",
    "methodFullName": "CoreClrSample.HotPath.SpinLoop"
  },
  "inclusiveSamples": 423,
  "inclusivePercent": 88.12,
  "identity": {
    "moduleVersionId": "1f5b2e84-8c3d-4e0a-9d27-a3c4f1f7a512",
    "metadataToken": 100663324,           // 0x06000020
    "moduleName": "CoreClrSample.dll",
    "modulePath": "/app/CoreClrSample.dll",
    "typeFullName": "CoreClrSample.HotPath",
    "methodName": "SpinLoop",
    "genericArity": 0
  }
}
```

The LLM can then drive the assembly MCP directly:

```
dotnet-assembly-mcp.load_assembly(path="/app/CoreClrSample.dll")
dotnet-assembly-mcp.get_method(
    moduleVersionId="1f5b2e84-8c3d-4e0a-9d27-a3c4f1f7a512",
    metadataToken="0x06000020",
    typeFullName="CoreClrSample.HotPath",   // sanity check
    methodName="SpinLoop")
dotnet-assembly-mcp.decompile_method(moduleVersionId="...", metadataToken="0x06000020")
dotnet-assembly-mcp.find_callers(moduleVersionId="...", metadataToken="0x06000020")
```

No string parsing, no fuzzy matching — and the IL token is the same whether the LLM
arrived here from a CPU sample, an exception stack, or a future GC artifact.

## Error kinds

| Situation                                          | Producer behaviour                                  | Consumer guidance                                   |
|----------------------------------------------------|-----------------------------------------------------|-----------------------------------------------------|
| Sidecar can't read PE off disk (file not found, PE corrupted, no metadata) | `ModuleVersionId = null`, other fields still emitted | Cannot handoff — surface a `module_not_resolvable` hint pointing the LLM to provide a path |
| Native-only frame                                  | Skip from `MethodIdentities`                        | n/a (no entry, no handoff)                          |
| Token reported as `0`                              | `MetadataToken = null`                              | Cannot handoff — display label only                 |
| MVID mismatch on consumer side                     | n/a                                                 | Return `module_not_loaded`; ask for explicit `load_assembly` |

## Versioning

The schema is part of the diagnostic summary (`schemaVersion` field on the exported
investigation summary). Additive fields on `MethodIdentity` are non-breaking; field
removals or semantic changes require a `schemaVersion` bump.
