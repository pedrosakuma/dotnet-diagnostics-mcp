namespace DotnetDiagnosticsMcp.Core.JitCapture;

/// <summary>
/// Inputs to <see cref="IJitMethodCapturer"/>. The <c>(ModuleVersionId, MetadataToken)</c>
/// pair is the canonical handoff key (matches <c>MethodIdentity</c>) and uniquely identifies
/// a method-definition row in the PE metadata regardless of name mangling, generics, or
/// compiler-synthesised closure names.
/// </summary>
/// <param name="ModuleVersionId">PE module MVID of the declaring assembly.</param>
/// <param name="MetadataToken">IL method-def metadata token (table 0x06).</param>
/// <param name="CodeAddress">Optional override: a code address the caller already observed
/// (e.g. from a <c>MethodLoad_V2</c> event). When supplied, the capturer uses
/// <c>ClrRuntime.GetMethodByInstructionPointer</c> as a fast path; mismatches with
/// <paramref name="ModuleVersionId"/>/<paramref name="MetadataToken"/> surface as a warning,
/// not a hard error.</param>
/// <param name="Tier">User-supplied tier label echoed back on the result
/// (e.g. <c>Tier0</c>, <c>Tier1</c>, <c>Tier1OSR</c>). ClrMD only exposes
/// <c>MethodCompilationType</c> (None / Jit / Ngen) — the OptimizationTier is observed
/// at JIT-event time by the caller.</param>
/// <param name="OutputDirectory">Directory where <c>.bin</c> files are written. Defaults
/// to <c>{TempPath}/dotnet-diagnostics-mcp/method-bytes/{pid}/</c>.</param>
public sealed record MethodCaptureRequest(
    Guid ModuleVersionId,
    int MetadataToken,
    ulong? CodeAddress = null,
    string? Tier = null,
    string? OutputDirectory = null);
