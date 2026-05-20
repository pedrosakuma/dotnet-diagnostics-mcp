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
| `GenericTypeArguments` | `GenericInstantiation?` | closed-signature drilldown | Closed instantiation when the producer can extract it structurally from the trace — see below. `null` for non-generic methods AND for any frame where the producer couldn't recover the instantiation (consumer falls back to the open def) |
| `Source`          | `SourceLocation?` | inline source jump  | `{ File, StartLine, SourceLink?, EndLine? }` resolved locally from the PDB embedded in (or sitting next to) the module on the diagnostics box. When non-null the LLM can open `File:StartLine` directly — the partner `dotnet-assembly-mcp.get_method_source` becomes optional. `null` when no PDB is reachable, the method has no non-hidden sequence points (compiler-generated bodies), or source resolution was explicitly disabled (issue #28) |

### `GenericInstantiation` (issue #21)

| Field    | Type            | Description                                                                          |
|----------|-----------------|--------------------------------------------------------------------------------------|
| `Type`   | `string[]`      | Declaring type's closed type-args (length matches the open type's generic arity)     |
| `Method` | `string[]`      | Method's own closed type-args (length matches `GenericArity`)                        |

Type-arg strings are CLR reflection-style full names with **no assembly qualification**:
nested types use `+`, arrays use `T[]` / `T[,]`, nested generics keep their backtick-arity
form (e.g. `System.Collections.Generic.List`1[System.Int32]`). Either list may be empty
when only the other axis is generic; both lists are never null when the parent
`GenericTypeArguments` is non-null.

This is the producer-side companion of `dotnet-assembly-mcp`'s §3.5 closed-signature
resolution (`get_method` with `genericTypeArguments` / `genericMethodArguments`). When
emitted, two distinct closed instantiations of the same `MethodDef` arrive as **two
separate** `MethodIdentity` rows so the LLM can ask the assembly MCP to render each one's
closed signature independently.

> **Shared generics (`System.__Canon`).** The CLR JITs **one body** that is shared across
> all reference-type instantiations of a generic, parameterised by a runtime "canon"
> placeholder. The producer surfaces whatever the trace emits, which means
> `Box<string>` typically arrives as `Box<System.__Canon>` — not `Box<System.String>` —
> while value-type instantiations are unique (`Box<int>` → `Box<System.Int32>`).
> Consumers should accept `System.__Canon` as a valid reference-type arg and pass it
> through verbatim to `dotnet-assembly-mcp`'s §3.5 fast-path; the assembly MCP knows to
> treat `System.__Canon` as "any reference type" for display purposes. This is a runtime
> artefact, not a bug in the handoff.

> **Linux EventPipe — method-level closed args are not recoverable.** Confirmed runtime
> limitation: the `MethodLoadVerbose_V2` payload on Linux EventPipe carries the **open** IL
> signature only (e.g. `generic !!0 (!!0)` for `Echo<T>(T)` — `!!N` is method-type-param N).
> The closed type arguments (`int`, `string`, `__Canon`, …) are not in any EventPipe event
> payload — multiple JIT'd bodies of the same generic method show up as distinct
> `MethodStartAddress` entries with identical `(MethodToken, Namespace, Name, Signature)`
> tuples. As a result, a static generic method like `GenericFixture.Echo<int>` arrives as
> plain `GenericFixture.Echo` with `GenericArity = 0` and `GenericTypeArguments = null` on
> Linux. Consumers should still resolve such methods to their open `MethodDef` via
> `(mvid, token)`. Tracked as won't-fix in
> [issue #85](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/85); an opt-in
> ClrMD-backed enrichment path is being explored separately (see #85 closing comment).
> Type-level instantiations (e.g. `Box<int>`) are unaffected — the runtime-canonical
> `` `1[System.Int32] `` mangling is baked into the type name itself.

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

## TypeIdentity (dump inspection)

`inspect_dump` emits a sibling identity shape on top retained types so the LLM can
hand off straight from a heap walk to `dotnet-assembly-mcp` without parsing type
names.

```
TypeIdentity {
  ModuleVersionId : Guid     // MVID of the defining managed module
  MetadataToken   : int      // TypeDef token (table 0x02)
  ModuleName      : string   // file name only
  ModulePath      : string?  // best-effort full path (resolves PDB / symbols)
  TypeFullName    : string   // namespace + name, display-only sanity check
}
```

Same producer/consumer responsibilities as `MethodIdentity`: the `(ModuleVersionId,
MetadataToken)` pair is the trust boundary; everything else is a display label.
ClrMD reads the MVID directly from the loaded module path (cached per path); when
the file is unreachable, `ModuleVersionId` is `null` and the consumer must refuse
the handoff with `module_not_resolvable`.

The intended consumer hop today is `dotnet-assembly-mcp.load_assembly` followed by
inspection of the type's methods via `get_method` for each token of interest. A
future `get_type(mvid, typeToken)` tool on the assembly side will close the loop.
