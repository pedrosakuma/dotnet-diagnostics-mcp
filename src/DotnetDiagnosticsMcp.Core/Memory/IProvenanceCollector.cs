namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// Source of provenance information surfaced in <see cref="InvestigationSummary"/>. Default
/// implementation reads K8s downward-API env vars set on the sidecar (the sidecar shares the
/// pod namespace with the target). Tests can swap in a fixed snapshot for determinism.
/// </summary>
public interface IProvenanceCollector
{
    InvestigationProvenance Collect(int processId, string? buildAssemblyName = null);
}

public sealed class EnvironmentProvenanceCollector : IProvenanceCollector
{
    public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
    {
        var container = ReadContainer();
        var build = ReadBuild(buildAssemblyName);
        var host = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        return new InvestigationProvenance(host) { Build = build, Container = container };
    }

    private static ContainerProvenance? ReadContainer()
    {
        // Conventional names recommended by the K8s downward API documentation. When none of
        // these are present we report null so consumers can branch on "no container provenance".
        var image = Env("CONTAINER_IMAGE", "IMAGE_REF", "POD_IMAGE");
        var ns = Env("POD_NAMESPACE", "KUBERNETES_NAMESPACE");
        var pod = Env("POD_NAME", "HOSTNAME"); // pod name often equals HOSTNAME inside the container
        var node = Env("NODE_NAME");

        if (image is null && ns is null && pod is null && node is null) return null;
        return new ContainerProvenance(image, ns, pod, node);
    }

    private static BuildProvenance? ReadBuild(string? assemblyName)
    {
        // We don't reach into the target process — that requires symbol resolution / module
        // enumeration we haven't built yet. The LLM passes the assembly name it learned from
        // list_dotnet_processes / get_process_info; we surface that verbatim. Future work
        // (#11 source-resolution) will fill in InformationalVersion + GitSha by reading PE
        // headers / ResourceManager.
        if (string.IsNullOrWhiteSpace(assemblyName)) return null;
        return new BuildProvenance(
            AssemblyName: assemblyName,
            AssemblyVersion: null,
            InformationalVersion: null,
            GitSha: null,
            TargetFramework: null);
    }

    private static string? Env(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
