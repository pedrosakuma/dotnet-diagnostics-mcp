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

## Path hints are untrusted

Every path-shaped field in this contract — `ModulePath` on `MethodIdentity` /
`TypeIdentity`, `imagePath` on `NativeFrame`, the `dumpFilePath` argument on
`inspect_heap(source="dump")`, and any future `mstatPath` / `dgmlPath` / `ilMapPath` hint — is
**a best-effort hint based on what the producer observed at runtime**. It is
**not** an authenticated assertion that the file at that path is the artifact
the consumer should load.

The LLM sits in the middle of every handoff. A compromised, jailbroken, or
simply mistaken model can swap the path for anything reachable on the consumer
host (`/etc/shadow`, an attacker-staged `evil.dll` under `/tmp`, a UNC path,
`..`-traversal, a symlink to a sensitive directory) and the consumer would
otherwise happily open it. The MVID / build-id half of the handoff defends
against this — but only if the consumer actually checks it.

### Producer behaviour

- Producers MAY emit `ModulePath` / `imagePath` etc. as a convenience for human
  readers and as a *hint* the consumer can use to locate the artifact.
- Producers **SHOULD NOT** imply the path is safe to open. The
  `(moduleVersionId, metadataToken)` / `(buildId, …)` half of the payload is the
  authoritative identity; the path is supplementary.
- Producers SHOULD prefer emitting the artifact's content-addressable identity
  (MVID for managed PEs, GNU build-id / PE CodeView GUID+Age / Mach-O LC_UUID
  for native images) on every record where one is recoverable.

### Consumer behaviour (MUST)

Consumer MCPs (`dotnet-assembly-mcp`, `dotnet-native-mcp`, and any future
sibling that grows a `load_*` / `import_*_manifest` / `inspect_*` tool that
takes a filesystem path) **MUST** treat handoff path hints as *display-only*
until they have:

1. **Canonicalised** the path with `Path.GetFullPath` **and** resolved every
   reparse point along the way — on POSIX via `realpath` / a
   `File.ResolveLinkTarget` walk, on Windows by resolving NTFS symlinks,
   junctions, and reparse points the same way (`Path.GetFullPath` alone does
   *not* follow Windows reparse points, so a junction inside an allowed root
   can otherwise still escape). The result is the path's true on-disk target,
   with `..`-traversal and link tricks flattened, before any policy check.
2. **Allowlisted** the canonicalised result against a fixed, configured set of
   trusted roots — for example:
   - the running process's own loaded-module list (when the consumer is
     co-located with the target),
   - the platform R2R / NativeAOT cache (`%ProgramFiles%\dotnet\shared\…`,
     `/usr/share/dotnet/…`),
   - the NuGet global packages cache (`~/.nuget/packages`,
     `%USERPROFILE%\.nuget\packages`),
   - operator-configured deployment roots (e.g. `/app`, `/assemblies`,
     `/binaries` — the volumes already mounted into the sidecar).
3. **Rejected symlink escapes**: if any path component after canonicalisation
   resolves outside the allowlisted roots, refuse with a structured error
   (`path_not_allowed` / `path_outside_allowlist`) and surface the rejected
   canonical path in the error envelope. Do not fall back to opening the file
   anyway.
4. **Verified content identity before any load**: open the file with metadata-
   only readers (`PEReader` / `ELFReader` / `MachOReader`), extract the MVID or
   build-id, and compare it byte-for-byte against the identity the producer
   sent. On mismatch, refuse with `mvid_mismatch_with_path` /
   `build_id_mismatch_with_path` — never silently re-map the identity to
   whatever was on disk.

### Recommended consumer pattern

> **Prefer identity-first lookup; treat the path as a fallback.**

Consumers SHOULD first try to satisfy the request from their own already-loaded
module / image index keyed by MVID / build-id. The path is consulted only when
the identity is not yet known to the consumer **and** the path survives the
four checks above. This pattern naturally protects every handoff endpoint —
including the shipped cross-MCP byte-fetch tools (`get_bytes(kind="module")` /
`get_bytes(kind="dump")`, [#144](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/144)) — without
spreading validation logic across every tool entry-point.

### Worked example

**Anti-pattern (do not do this).** The consumer trusts the path as-is:

```csharp
// ❌ Vulnerable: an LLM-supplied modulePath can point anywhere.
[McpTool("load_assembly")]
public LoadResult LoadAssembly(string path)
{
    using var pe = new PEReader(File.OpenRead(path));   // opens arbitrary file
    var mvid = pe.GetMetadataReader().GetModuleDefinition().Mvid;
    _index[mvid] = path;                                 // trusts whatever was there
    return new LoadResult(mvid, path);
}
```

**Correct pattern.** Identity-first; path is a hint, validated then verified:

```csharp
// ✅ Identity-first, path-validated, MVID-verified.
[McpTool("load_assembly")]
public LoadResult LoadAssembly(string path, Guid? expectedMvid = null)
{
    // 1. If we already have this identity, ignore the path entirely.
    if (expectedMvid is { } known && _index.TryGet(known, out var loaded))
        return new LoadResult(known, loaded.Path);

    // 2. Canonicalise AND resolve reparse points (symlinks, NTFS junctions, …)
    //    on every platform — Path.GetFullPath alone does not follow them.
    var canonical = ResolveRealPathOrThrow(Path.GetFullPath(path));

    // 3. Allowlist roots — boundary-aware "is canonical under an allowed root"
    //    check (NOT a flat Set.Contains, which would only match the root dir
    //    itself, and NOT a substring match, which would be unsafe).
    if (!IsUnderAllowedRoot(canonical, _allowedRoots))
        throw new McpError("path_not_allowed", canonical);

    // 4. Read metadata-only, verify MVID matches the producer-supplied identity.
    using var pe = new PEReader(File.OpenRead(canonical));
    var reader = pe.GetMetadataReader();
    var actualMvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
    if (expectedMvid is { } want && want != actualMvid)
        throw new McpError("mvid_mismatch_with_path",
            $"hint={canonical} expected={want} actual={actualMvid}");

    _index[actualMvid] = canonical;
    return new LoadResult(actualMvid, canonical);
}

// Boundary-aware containment: each allowed root is canonicalised once at
// startup; the candidate must match a root exactly OR sit beneath it with a
// directory separator at the boundary (so /assemblies-secret is NOT under
// /assemblies). Case-insensitive on Windows / macOS, case-sensitive on Linux.
static bool IsUnderAllowedRoot(string canonical, IReadOnlyList<string> roots)
{
    var cmp = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;
    foreach (var root in roots)
    {
        if (canonical.Equals(root, cmp)) return true;
        if (canonical.Length > root.Length
            && canonical.StartsWith(root, cmp)
            && canonical[root.Length] == Path.DirectorySeparatorChar)
            return true;
    }
    return false;
}
```

The same shape applies to native consumers (`load_native_binary`,
`disassemble(imagePath=…)`, `import_native_manifest`, `explain_retention(dgmlPath=…)`,
`get_size_breakdown(mstatPath=…)`, `disassemble(ilMapPath=…)`) and to any future
tool that accepts a filesystem path off the wire — substitute `buildId` for
`MVID` and `ELFReader` / `MachOReader` / `PEReader` for the metadata reader.

## `MethodIdentity` (full payload)

`dotnet-diagnostics-mcp` emits this record on every ranked hotspot of `collect_sample(kind="cpu")`
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
| `ClosedSignature` | `string?` | display | Best-effort normalized closed-signature display string (for example `MyApp.Handler`1[System.Int32].Handle<System.String>`). Display-only; the canonical closed-signature handoff remains `GenericTypeArguments` + `(moduleVersionId, metadataToken)`. |
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

> **Linux EventPipe — method-level closed args need opt-in enrichment.** Confirmed runtime
> limitation: the `MethodLoadVerbose_V2` payload on Linux EventPipe carries the **open** IL
> signature only (e.g. `generic !!0 (!!0)` for `Echo<T>(T)` — `!!N` is method-type-param N).
> The closed type arguments (`int`, `string`, `__Canon`, …) are not in any EventPipe event
> payload — multiple JIT'd bodies of the same generic method show up as distinct
> `MethodStartAddress` entries with identical `(MethodToken, Namespace, Name, Signature)`
> tuples. By default `collect_sample(kind="cpu")` therefore still emits the open method for such
> frames. When the caller sets `resolveMethodInstantiations=true`, the producer performs a
> second **ClrMD** attach after sampling, walks the hottest frames by instruction pointer,
> and back-fills `GenericTypeArguments.Method` + `ClosedSignature` from the resolved closed
> runtime signature. On Linux that path requires `CAP_SYS_PTRACE` (or `ptrace_scope=0`) and
> briefly suspends the target. Type-level instantiations (e.g. `Box<int>`) are unaffected —
> the runtime-canonical `` `1[System.Int32] `` mangling is baked into the type name itself.

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
   addressable cache). **`modulePath` is an untrusted display-only hint** — see
   [Path hints are untrusted](#path-hints-are-untrusted) above; consumers MUST
   canonicalise, allowlist, and verify MVID before opening it.
3. Surface a clear error if no loaded module matches the MVID — the LLM can then load the
   assembly explicitly via `load_assembly(path)` using the producer-supplied `modulePath`
   as a hint, subject to the same canonicalise / allowlist / MVID-verify rules.

## Worked example

After `collect_sample(kind="cpu")` returns, a top hotspot might look like (truncated):

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

`inspect_heap(source="dump")` emits a sibling identity shape on top retained types so the LLM can
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
