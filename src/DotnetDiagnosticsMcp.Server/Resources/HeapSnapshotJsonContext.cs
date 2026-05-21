using System.Text.Json.Serialization;
using DotnetDiagnosticsMcp.Core.Dump;

namespace DotnetDiagnosticsMcp.Server.Resources;

internal sealed record HeapSnapshotErrorPayload(string Kind, string Error);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HeapSnapshotArtifact))]
[JsonSerializable(typeof(HeapSnapshotErrorPayload))]
[JsonSerializable(typeof(TypeStat))]
[JsonSerializable(typeof(TypeIdentity))]
[JsonSerializable(typeof(RetentionPath))]
[JsonSerializable(typeof(RetentionFrame))]
[JsonSerializable(typeof(DumpRuntimeInfo))]
[JsonSerializable(typeof(DumpHeapSummary))]
[JsonSerializable(typeof(RootKindStat))]
[JsonSerializable(typeof(FinalizableTypeStat))]
[JsonSerializable(typeof(SegmentStat))]
[JsonSerializable(typeof(StaticFieldStat))]
[JsonSerializable(typeof(DelegateTargetStat))]
[JsonSerializable(typeof(DuplicateStringStat))]
[JsonSerializable(typeof(AsyncOperationStat))]
[JsonSerializable(typeof(AsyncChainFrame))]
internal sealed partial class HeapSnapshotJsonContext : JsonSerializerContext;
