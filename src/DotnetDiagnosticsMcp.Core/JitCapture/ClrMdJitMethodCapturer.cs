using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetDiagnosticsMcp.Core.Artifacts;
using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.JitCapture;

/// <summary>
/// ClrMD-backed <see cref="IJitMethodCapturer"/>. Resolves the requested method by its
/// canonical <c>(MVID, MetadataToken)</c> key, reads the JIT'd Hot/Cold regions out of
/// the runtime's code-heap via <see cref="ClrMethod.HotColdInfo"/>, and writes each
/// region as a header-less raw blob ready for <c>dotnet-native-mcp.disassemble(rawBlob=true)</c>.
/// Works uniformly against a live process (via <c>DataTarget.AttachToProcess</c>)
/// and against a previously-captured dump (via <c>DataTarget.LoadDump</c>).
/// </summary>
/// <remarks>
/// NativeAOT and pure-ReadyToRun targets are not supported: there is no JIT code-heap to
/// read from — disassemble the on-disk image with <c>dotnet-native-mcp.disassemble</c> instead.
/// The capturer surfaces this as an <see cref="InvalidOperationException"/> at attach time.
/// </remarks>
public sealed class ClrMdJitMethodCapturer : IJitMethodCapturer
{
    private const int DefaultReadChunkBytes = 4096;
    private readonly IArtifactRootProvider _artifactRoot;
    private readonly ILogger<ClrMdJitMethodCapturer> _logger;
    private readonly ConcurrentDictionary<string, Guid?> _mvidCache = new(StringComparer.Ordinal);

    public ClrMdJitMethodCapturer(
        IArtifactRootProvider artifactRoot,
        ILogger<ClrMdJitMethodCapturer>? logger = null)
    {
        _artifactRoot = artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot));
        _logger = logger ?? NullLogger<ClrMdJitMethodCapturer>.Instance;
    }

    public Task<CapturedMethodBytes> CaptureLiveAsync(
        int processId,
        MethodCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() =>
        {
            CapturedMethodBytes artifact;
            IReadOnlyList<PendingWrite> writes;
            // suspend=true mirrors ClrMdDumpInspector / ClrMdThreadSnapshotInspector. We keep
            // the window minimal by deferring all disk I/O to after Dispose — only the
            // ClrMD walk + the chunked memory reads happen while the target is suspended.
            using (var target = DataTarget.AttachToProcess(processId, suspend: true))
            {
                (artifact, writes) = Capture(target, request, CapturedMethodBytesOrigin.Live, cancellationToken);
            }
            FinalizeWrites(artifact.OutputDirectory, writes);
            return artifact;
        }, cancellationToken);
    }

    public Task<CapturedMethodBytes> CaptureFromDumpAsync(
        string dumpFilePath,
        MethodCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpFilePath);
        if (!File.Exists(dumpFilePath)) throw new FileNotFoundException("Dump file not found.", dumpFilePath);
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() =>
        {
            CapturedMethodBytes artifact;
            IReadOnlyList<PendingWrite> writes;
            using (var target = DataTarget.LoadDump(dumpFilePath))
            {
                (artifact, writes) = Capture(target, request, CapturedMethodBytesOrigin.Dump, cancellationToken);
            }
            FinalizeWrites(artifact.OutputDirectory, writes);
            return artifact;
        }, cancellationToken);
    }

    private static void FinalizeWrites(string outputDir, IReadOnlyList<PendingWrite> writes)
    {
        if (writes.Count == 0) return;
        // Note: outputDir was already created with restrictive permissions inside
        // ResolveOutputDirectory; no second CreateDirectory pass is needed (and doing
        // it again would re-validate the path through a symlinked parent on a TOCTOU
        // race).
        foreach (var w in writes)
        {
            // CreateRestrictedFile uses FileMode.CreateNew (refuses an existing
            // symlink at the leaf) and FileStreamOptions.UnixCreateMode=0600 so the
            // file is born with restrictive permissions — no umask race.
            using var fs = SafeArtifactPath.CreateRestrictedFile(w.Path);
            fs.Write(w.Bytes, 0, w.Bytes.Length);
        }
    }

    private readonly record struct PendingWrite(string Path, byte[] Bytes);

    private (CapturedMethodBytes Artifact, IReadOnlyList<PendingWrite> Writes) Capture(
        DataTarget target,
        MethodCaptureRequest request,
        CapturedMethodBytesOrigin origin,
        CancellationToken ct)
    {
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Target does not expose a CoreCLR runtime — capture_method_bytes requires a JIT-emitted code-heap. " +
                "For NativeAOT or pure-ReadyToRun images, disassemble the on-disk binary with dotnet-native-mcp.disassemble.");
        using var runtime = clrInfo.CreateRuntime();

        var reader = target.DataReader;
        var pid = unchecked((int)reader.ProcessId);
        var arch = reader.Architecture.ToString();
        var runtimeName = clrInfo.Flavor.ToString();
        var runtimeVersion = clrInfo.Version.ToString();
        var warnings = new List<string>();
        var outputDir = ResolveOutputDirectory(request, pid);

        var method = ResolveMethod(runtime, request, warnings, ct);
        if (method is null)
        {
            throw new InvalidOperationException(
                $"Method (mvid={request.ModuleVersionId:D}, metadataToken=0x{request.MetadataToken:X8}) was not found in the target runtime. " +
                "Verify the module is loaded in the target and the token refers to a method-def (table 0x06).");
        }

        var identity = BuildIdentity(method);
        var hci = method.HotColdInfo;
        var hotStart = hci.HotStart;
        var hotSize = unchecked((int)hci.HotSize);
        var coldStart = hci.ColdStart;
        var coldSize = unchecked((int)hci.ColdSize);

        if (hotStart == 0 || hotSize <= 0)
        {
            warnings.Add(
                "Method has no JIT-emitted code (HotStart=0 or HotSize=0). " +
                "Possible causes: abstract/extern method, not yet JITted on the target's current execution path, " +
                "or ReadyToRun-only with no live thunk. Trigger an execution path that calls the method, then retry.");
            return (new CapturedMethodBytes(origin, pid, runtimeName, runtimeVersion, arch, identity,
                Array.Empty<MethodBytesRef>(), outputDir, warnings), Array.Empty<PendingWrite>());
        }

        var regions = new List<MethodBytesRef>(2);
        var writes = new List<PendingWrite>(2);
        AppendRegion(reader, hotStart, hotSize, "Hot", request, identity, method, arch, outputDir, regions, writes, warnings, ct);
        if (coldStart != 0 && coldSize > 0)
        {
            AppendRegion(reader, coldStart, coldSize, "Cold", request, identity, method, arch, outputDir, regions, writes, warnings, ct);
        }

        return (new CapturedMethodBytes(
            origin, pid, runtimeName, runtimeVersion, arch,
            identity, regions, outputDir,
            warnings.Count > 0 ? warnings : null), writes);
    }

    private ClrMethod? ResolveMethod(
        ClrRuntime runtime,
        MethodCaptureRequest req,
        List<string> warnings,
        CancellationToken ct)
    {
        // Fast path: caller already saw a MethodLoad_V2 / JITStartedV2 event and knows a
        // current code address — GetMethodByInstructionPointer is O(1) on the runtime's
        // jit-code lookup table and skips the full type walk.
        ClrMethod? viaIp = null;
        if (req.CodeAddress is ulong ip && ip != 0)
        {
            viaIp = runtime.GetMethodByInstructionPointer(ip);
            if (viaIp is null)
            {
                warnings.Add($"codeAddress 0x{ip:X} did not resolve to any JIT'd method in the target; falling back to MVID+token lookup.");
            }
            else if (viaIp.MetadataToken != req.MetadataToken)
            {
                warnings.Add(
                    $"codeAddress 0x{ip:X} resolved to method token 0x{viaIp.MetadataToken:X8} which does not match the requested 0x{req.MetadataToken:X8}; " +
                    "ignoring the IP override and using MVID+token lookup. Pass a code address from the same method's MethodLoad_V2 event if you want the fast-path.");
                viaIp = null;
            }
        }

        if (viaIp is not null && req.CodeAddress is ulong ipOk)
        {
            // Verify the IP-resolved method's module matches the requested MVID, so we
            // don't return code from a same-token namesake in a different assembly.
            // Note: unverifiable MVID (null — dynamic module, single-file bundle, missing
            // or unreadable on-disk path) is NOT treated as a positive match. We fall
            // back to the full (MVID, token) walk to guarantee module identity.
            var ipModule = viaIp.Type?.Module;
            var ipMvid = ipModule is null ? null : TryReadMvid(ipModule.Name);
            if (ipMvid is { } known && known == req.ModuleVersionId)
            {
                return viaIp;
            }
            if (ipMvid is null)
            {
                warnings.Add(
                    $"codeAddress 0x{ipOk:X} resolved to a method in module {ipModule?.Name ?? "<unknown>"} whose MVID could not be verified " +
                    "(dynamic module, single-file bundle, or missing/unreadable on-disk image); falling back to MVID+token lookup to guarantee module identity.");
            }
            else
            {
                warnings.Add(
                    $"codeAddress 0x{ipOk:X} resolved to method in module {ipModule?.Name ?? "<unknown>"} (mvid={ipMvid:D}) " +
                    $"but request asked for mvid={req.ModuleVersionId:D}; ignoring the IP override.");
            }
        }

        ClrMethod? match = null;
        int matchCount = 0;
        foreach (var module in runtime.EnumerateModules())
        {
            ct.ThrowIfCancellationRequested();
            var path = module.Name;
            if (string.IsNullOrEmpty(path)) continue;

            var mvid = TryReadMvid(path);
            if (mvid is null || mvid.Value != req.ModuleVersionId) continue;

            foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                ct.ThrowIfCancellationRequested();
                if (mt == 0) continue;

                ClrType? type;
                try
                {
                    type = runtime.GetTypeByMethodTable(mt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Hardened DAC walks: a transient DAC error on one MT shouldn't abort
                    // the whole enumeration.
                    _logger.LogDebug(ex, "GetTypeByMethodTable failed for MT 0x{MethodTable:X}", mt);
                    continue;
                }

                if (type is null) continue;
                foreach (var m in type.Methods)
                {
                    if (m.MetadataToken != req.MetadataToken) continue;
                    matchCount++;
                    match ??= m;
                }
            }
        }

        if (matchCount > 1)
        {
            warnings.Add(
                $"Method token 0x{req.MetadataToken:X8} matched {matchCount} loaded MethodTables in module mvid={req.ModuleVersionId:D} " +
                "(likely a method on a generic type with multiple closed instantiations). Capturing the first match — " +
                "pass an explicit codeAddress observed for the desired instantiation to disambiguate.");
        }

        return match;
    }

    private static void AppendRegion(
        IDataReader reader,
        ulong start,
        int size,
        string region,
        MethodCaptureRequest req,
        MethodIdentity identity,
        ClrMethod method,
        string arch,
        string outputDir,
        List<MethodBytesRef> regions,
        List<PendingWrite> writes,
        List<string> warnings,
        CancellationToken ct)
    {
        var buffer = new byte[size];
        var totalRead = 0;
        // Read in modest chunks so a partial read on a torn dump doesn't lose everything.
        while (totalRead < size)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = size - totalRead;
            var span = buffer.AsSpan(totalRead, Math.Min(remaining, DefaultReadChunkBytes));
            var got = reader.Read(start + (ulong)totalRead, span);
            if (got <= 0) break;
            totalRead += got;
        }

        if (totalRead < size)
        {
            warnings.Add(
                $"{region} region truncated: requested {size:N0} byte(s) at 0x{start:X} but only {totalRead:N0} were readable. " +
                "On a live process this usually means the code-heap was unmapped mid-read (very rare); on a dump it means the dump is incomplete.");
        }

        var fileName = BuildFileName(identity, method, region, req.Tier);
        var path = Path.Combine(outputDir, fileName);
        var payload = totalRead == size ? buffer : buffer.AsSpan(0, totalRead).ToArray();

        regions.Add(new MethodBytesRef(
            FilePath: path,
            Size: totalRead,
            BaseAddress: unchecked((long)start),
            Architecture: arch,
            Region: region,
            Tier: req.Tier,
            CompilationType: method.CompilationType.ToString()));

        writes.Add(new PendingWrite(path, payload));
    }

    private string ResolveOutputDirectory(MethodCaptureRequest req, int pid)
    {
        // SafeArtifactPath rejects absolute paths and traversal/symlink escapes; the
        // default sub-path preserves the legacy per-pid layout when no caller override
        // is supplied. The returned path is always under the operator-configured root.
        var defaultRelative = Path.Combine(
            "method-bytes",
            pid.ToString(CultureInfo.InvariantCulture));
        return SafeArtifactPath.ResolveDirectory(
            _artifactRoot.Root,
            req.OutputDirectory,
            defaultRelative,
            parameterName: nameof(MethodCaptureRequest.OutputDirectory));
    }

    private static string BuildFileName(MethodIdentity identity, ClrMethod method, string region, string? tier)
    {
        // typeFqn.methodName — fall back to whatever ClrMD reported when the identity didn't
        // carry display strings.
        var typeDisplay = identity.TypeFullName ?? method.Type?.Name ?? "UnknownType";
        var methodDisplay = identity.MethodName ?? method.Name ?? "UnknownMethod";

        var sb = new StringBuilder(Sanitize(typeDisplay));
        sb.Append('.');
        sb.Append(Sanitize(methodDisplay));
        sb.Append('-');
        sb.Append(region);
        if (!string.IsNullOrEmpty(tier))
        {
            sb.Append('-');
            sb.Append(Sanitize(tier));
        }
        sb.Append("-0x").Append(identity.MetadataToken?.ToString("X8", CultureInfo.InvariantCulture) ?? "00000000");
        sb.Append(".bin");
        return sb.ToString();
    }

    private static string Sanitize(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var span = input.AsSpan();
        var sb = new StringBuilder(span.Length);
        foreach (var c in span)
        {
            // Replace anything that would break path semantics or read as a generic-arity
            // marker on the disassembler side.
            if (c == '<' || c == '>' || c == '`' || c == ',' || c == ' ' || c == '+' || Array.IndexOf(invalid, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private MethodIdentity BuildIdentity(ClrMethod method)
    {
        var type = method.Type;
        var modulePath = type?.Module?.Name;
        var moduleName = string.IsNullOrEmpty(modulePath) ? null : Path.GetFileName(modulePath);
        var mvid = TryReadMvid(modulePath);
        var token = method.MetadataToken;
        return new MethodIdentity(
            MethodName: method.Name ?? "<unknown>",
            GenericArity: 0,
            ModuleName: moduleName,
            ModulePath: modulePath,
            ModuleVersionId: mvid,
            MetadataToken: token != 0 ? token : null,
            TypeFullName: type?.Name);
    }

    private Guid? TryReadMvid(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        return _mvidCache.GetOrAdd(assemblyPath, static path =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata) return null;
                var mdReader = peReader.GetMetadataReader();
                return mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }
}
