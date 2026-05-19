using System.Collections.Concurrent;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Reads the <c>ModuleVersionId</c> (MVID) from a managed PE file on disk. The MVID is the
/// PE module's metadata identifier — stable across copies of the same binary and the only
/// reliable cross-MCP handoff key for a method (paired with its metadata token).
/// Reads are cached by absolute path so repeated lookups during a single sample cost nothing.
/// </summary>
public sealed class MvidReader
{
    private readonly ConcurrentDictionary<string, Guid?> _cache = new(StringComparer.Ordinal);

    public Guid? TryRead(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        return _cache.GetOrAdd(assemblyPath, ReadFromDisk);
    }

    private static Guid? ReadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return null;
            var metadata = peReader.GetMetadataReader();
            var moduleDef = metadata.GetModuleDefinition();
            return metadata.GetGuid(moduleDef.Mvid);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
