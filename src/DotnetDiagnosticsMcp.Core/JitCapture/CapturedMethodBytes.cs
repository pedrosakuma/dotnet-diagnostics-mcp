using DotnetDiagnosticsMcp.Core.Memory;

namespace DotnetDiagnosticsMcp.Core.JitCapture;

/// <summary>
/// Where the JIT-emitted bytes were sourced from.
/// </summary>
public enum CapturedMethodBytesOrigin
{
    Live,
    Dump,
}

/// <summary>
/// The full result of a <see cref="IJitMethodCapturer"/> call: which method was resolved
/// in the target runtime, every JIT region we managed to write to disk for it, and any
/// best-effort warnings (e.g. "Cold region present but read truncated — only N of M bytes
/// could be read from the code-heap"). Each <see cref="MethodBytesRef"/> entry is
/// independently consumable by <c>dotnet-native-mcp.disassemble(rawBlob=true)</c>.
/// </summary>
/// <param name="Origin">Live process vs offline dump.</param>
/// <param name="ProcessId">PID of the live target, or the embedded PID for dump origin.</param>
/// <param name="RuntimeName">e.g. <c>Core</c> — surfaced by <c>ClrInfo.Flavor</c>.</param>
/// <param name="RuntimeVersion">Runtime version string (<c>ClrInfo.Version</c>).</param>
/// <param name="Architecture">Process architecture (<c>X64</c>, <c>Arm64</c>, …).</param>
/// <param name="Method">The method whose IL <c>(MVID, MetadataToken)</c> we resolved against
/// the runtime — includes the type/method display names that ClrMD reported.</param>
/// <param name="Regions">One <see cref="MethodBytesRef"/> per region we wrote
/// (Hot, optionally Cold). Empty when the method has no JIT'd code (e.g. abstract,
/// not-yet-JITted, or ReadyToRun-only).</param>
/// <param name="OutputDirectory">The directory used to materialise the <c>.bin</c> files.</param>
/// <param name="Warnings">Best-effort diagnostics that don't fail the capture
/// (e.g. address override mismatch, partial read).</param>
public sealed record CapturedMethodBytes(
    CapturedMethodBytesOrigin Origin,
    int ProcessId,
    string RuntimeName,
    string RuntimeVersion,
    string Architecture,
    MethodIdentity Method,
    IReadOnlyList<MethodBytesRef> Regions,
    string OutputDirectory,
    IReadOnlyList<string>? Warnings = null);
