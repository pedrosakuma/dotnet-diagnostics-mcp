namespace DotnetDiagnosticsMcp.Core.JitCapture;

/// <summary>
/// One contiguous block of JIT-emitted native code captured to disk. Designed as the
/// payload for the cross-MCP handoff to <c>dotnet-native-mcp.disassemble</c> in
/// <c>rawBlob=true</c> mode (issue #81 + native-mcp#47): the receiver opens
/// <see cref="FilePath"/>, treats the entire file as raw machine code, and disassembles
/// <see cref="Size"/> bytes starting at offset 0 — emitting addresses relative to
/// <see cref="BaseAddress"/> so call/jmp targets line up with the original process VA
/// space.
/// </summary>
/// <param name="FilePath">Absolute path to the captured binary blob (no PE/ELF/Mach-O header).</param>
/// <param name="Size">Length of the captured block in bytes (matches the file's size on disk).</param>
/// <param name="BaseAddress">Original process VA of the first byte. Used by the disassembler
/// for absolute-address fixups.</param>
/// <param name="Architecture">CLR architecture string (<c>X64</c>, <c>X86</c>, <c>Arm64</c>, <c>Arm</c>).</param>
/// <param name="Region">Which JIT region these bytes came from: <c>Hot</c> or <c>Cold</c>.</param>
/// <param name="Tier">User-supplied tier label echoed back when caller passed one
/// (e.g. <c>Tier0</c>, <c>Tier1</c>, <c>Tier1OSR</c>). ClrMD does not expose the JIT
/// optimization tier directly, so this field is informational — the runtime-derived
/// <see cref="CompilationType"/> is authoritative.</param>
/// <param name="CompilationType">ClrMD's <c>MethodCompilationType</c> for the resolved method
/// (None / Jit / Ngen).</param>
public sealed record MethodBytesRef(
    string FilePath,
    int Size,
    long BaseAddress,
    string Architecture,
    string Region,
    string? Tier = null,
    string? CompilationType = null);
