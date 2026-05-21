namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>Detailed inspection of one managed object captured via a heap snapshot handle.</summary>
public sealed record HeapObjectInspection(
    ulong Address,
    string TypeFullName,
    long Size,
    string SegmentKind,
    string Generation)
{
    public bool IsArray { get; init; }
    public int? ArrayLength { get; init; }
    public IReadOnlyList<HeapArrayElement>? ArraySample { get; init; }
    public bool IsString { get; init; }
    public string? StringValue { get; init; }
    public bool StringValueTruncated { get; init; }
    public IReadOnlyList<HeapObjectField>? Fields { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>One field row from <see cref="HeapObjectInspection"/>.</summary>
public sealed record HeapObjectField(string Name, string TypeFullName, string Value)
{
    public ulong? ObjectAddress { get; init; }
    public string? ReferencedTypeFullName { get; init; }
}

/// <summary>One sampled array element from <see cref="HeapObjectInspection"/>.</summary>
public sealed record HeapArrayElement(int Index, string TypeFullName, string Value)
{
    public ulong? ObjectAddress { get; init; }
    public string? ReferencedTypeFullName { get; init; }
}

/// <summary>Shortest GC-root chain currently found for a target object.</summary>
public sealed record HeapGcRootInspection(
    ulong Address,
    string TypeFullName,
    IReadOnlyList<RetentionFrame> Chain,
    bool Truncated)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Transitive closure size rooted at one managed object.</summary>
public sealed record HeapObjectSizeInspection(
    ulong Address,
    string TypeFullName,
    long RetainedBytes,
    int ObjectCount,
    bool Truncated)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}
