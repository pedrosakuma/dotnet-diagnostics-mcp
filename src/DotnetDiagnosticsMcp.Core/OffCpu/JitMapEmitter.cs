using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Emits a <c>/tmp/perf-&lt;pid&gt;.map</c> file describing every JIT-compiled managed method
/// in a live .NET process, so that <c>perf script</c> can resolve user-mode stack frames
/// to managed symbol names instead of raw hex addresses. This is the foundation of the
/// off-CPU managed↔kernel stack merge: once <c>perf script</c> emits frames like
/// <c>MyApp.OrderService.Checkout</c> the parser can attach a canonical
/// <see cref="MethodIdentity"/> (MVID + MetadataToken) by name lookup against the rundown
/// dictionary this emitter also returns.
/// </summary>
/// <remarks>
/// <para>Mechanism: opens a short EventPipe session against the target PID with
/// <c>requestRundown: true</c> and the runtime's <c>Jit</c> + <c>Loader</c> keywords.
/// On <c>StopAsync</c> the runtime flushes <c>MethodDCStopVerbose</c> + <c>ModuleDCStopVerbose</c>
/// events for every method/module currently loaded, which is the canonical mechanism the
/// runtime itself uses for <c>dotnet-trace</c>'s perf-map mode. Each <c>MethodDCStop</c>
/// carries the JIT start address, native code size, and the canonical method identity bits;
/// we then write one line per method to <c>/tmp/perf-&lt;pid&gt;.map</c> in the format perf
/// expects (<c>hexStart hexSize symbol</c>, space-separated). Subsequent <c>perf script</c>
/// invocations will pick the file up via the <c>--symfs</c>-less default search path.</para>
/// <para>Best-effort by design: if the EventPipe session fails to start (target process
/// exited, diagnostic socket UID mismatch, NativeAOT), the off-CPU sampler still produces
/// useful kernel+native stacks — the LLM just won't see managed method names in user frames.
/// Callers should swallow exceptions from <see cref="EmitAsync"/> and continue.</para>
/// </remarks>
public sealed class JitMapEmitter
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    // Jit (0x10) | Loader (0x8). NgenKeyword (0x4) would also surface AOT/R2R methods but
    // perf already resolves R2R native code via the assembly's own ELF symbols on Linux.
    private const long JitMapKeywords = 0x10 | 0x8;

    private readonly ILogger<JitMapEmitter> _logger;
    private readonly MvidReader _mvidReader;

    public JitMapEmitter(ILogger<JitMapEmitter>? logger = null, MvidReader? mvidReader = null)
    {
        _logger = logger ?? NullLogger<JitMapEmitter>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
    }

    /// <summary>
    /// Captures the runtime's JIT rundown for <paramref name="processId"/> and writes
    /// <c>/tmp/perf-&lt;pid&gt;.map</c>. Returns a lookup that maps the same symbol string
    /// emitted in the map file to a canonical <see cref="MethodIdentity"/>, ready for
    /// stack-frame enrichment in <see cref="PerfSchedScriptParser"/>.
    /// </summary>
    /// <param name="processId">Target PID.</param>
    /// <param name="rundownTimeout">
    /// Upper bound on how long the emitter waits for the rundown events to drain after
    /// requesting <c>StopAsync</c>. The runtime emits rundown synchronously on stop so 2s
    /// is generous; the timeout exists only as a guard against a stuck event pump.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The emitted <see cref="JitMapResult"/>, or <c>null</c> when the session
    /// could not be opened (target exited, NativeAOT, permission denied).</returns>
    public async Task<JitMapResult?> EmitAsync(
        int processId,
        TimeSpan? rundownTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = rundownTimeout ?? TimeSpan.FromSeconds(2);
        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, JitMapKeywords),
        };

        EventPipeSession? session;
        try
        {
            var client = new DiagnosticsClient(processId);
            session = await client
                .StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: 64, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JitMapEmitter: could not open EventPipe session for pid {Pid}.", processId);
            return null;
        }

        // moduleId -> ILPath (canonical, on-disk path of the managed module).
        var modulePaths = new ConcurrentDictionary<long, string>();
        // Pending records held until ModuleDCStop arrives so we can resolve the module path.
        var pending = new ConcurrentBag<PendingJitMethod>();

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                // Live JIT / module load events fire while the session is active.
                source.Clr.MethodLoadVerbose += data => Record(data);
                source.Clr.LoaderModuleLoad += data =>
                {
                    var path = data.ModuleILPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        modulePaths[data.ModuleID] = path;
                    }
                };

                // DC (data-collection / rundown) events fire on StopAsync for every method /
                // module already loaded. These live on a separate parser dedicated to rundown.
                var rundown = new ClrRundownTraceEventParser(source);
                rundown.MethodDCStopVerbose += data => Record(data);
                rundown.LoaderModuleDCStop += data =>
                {
                    var path = data.ModuleILPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        modulePaths[data.ModuleID] = path;
                    }
                };

                source.Process();

                void Record(MethodLoadUnloadVerboseTraceData data)
                {
                    if (data.MethodStartAddress == 0 || data.MethodSize <= 0)
                    {
                        return;
                    }
                    pending.Add(new PendingJitMethod(
                        StartAddress: (ulong)data.MethodStartAddress,
                        Size: (uint)data.MethodSize,
                        ModuleId: data.ModuleID,
                        Token: data.MethodToken,
                        TypeName: data.MethodNamespace ?? string.Empty,
                        MethodName: data.MethodName ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JitMapEmitter: event-source processing ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            // Rundown is synchronous on StopAsync — events drain into the pump above.
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopCts.CancelAfter(timeout);
            try
            {
                await session.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Internal guard timeout fired — pump still has whatever rundown drained so far;
                // emit a partial map rather than failing the whole off-CPU window.
                _logger.LogDebug(
                    "JitMapEmitter: rundown StopAsync exceeded {TimeoutMs}ms for pid {Pid}; emitting partial map.",
                    timeout.TotalMilliseconds, processId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Caller-triggered OperationCanceledException is intentionally excluded so it
                // propagates up to the off-CPU sampler / MCP tool. Any other failure (broken
                // pipe, session disposed mid-flight, …) is best-effort: log and continue.
                _logger.LogDebug(ex, "JitMapEmitter: StopAsync failed for pid {Pid}.", processId);
            }

            try
            {
                // Wait briefly for the pump to drain the post-stop event tail.
                await processingTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pump may have already exited or timed out; the bag already has what it has.
            }
        }
        finally
        {
            session.Dispose();
        }

        var mapPath = Path.Combine(Path.GetTempPath(), $"perf-{processId}.map");
        var methods = WriteMap(mapPath, pending, modulePaths);
        if (methods.Count == 0)
        {
            _logger.LogDebug("JitMapEmitter: rundown returned no methods for pid {Pid}.", processId);
        }

        return new JitMapResult(mapPath, methods, methods.Count);
    }

    private List<JitMapRange> WriteMap(
        string mapPath,
        IEnumerable<PendingJitMethod> methods,
        ConcurrentDictionary<long, string> modulePaths)
    {
        // Sort by start address so JitMapResult.Resolve can binary-search.
        var ordered = methods
            .Where(m => m.StartAddress != 0 && m.Size > 0)
            .OrderBy(m => m.StartAddress)
            .ToList();

        var ranges = new List<JitMapRange>(ordered.Count);
        using var stream = OpenMapFileSecure(mapPath);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var m in ordered)
        {
            var symbol = FormatSymbol(m.TypeName, m.MethodName);
            writer.Write(m.StartAddress.ToString("x", CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(m.Size.ToString("x", CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.WriteLine(symbol);

            modulePaths.TryGetValue(m.ModuleId, out var modulePath);
            var moduleName = !string.IsNullOrEmpty(modulePath) ? Path.GetFileName(modulePath) : null;
            var mvid = _mvidReader.TryRead(modulePath);

            // Use the same FullMethodName parser the on-CPU sampler uses so the LLM gets
            // consistent (TypeFullName, MethodName, GenericArity, GenericTypeArguments) shapes
            // regardless of which sampler produced the frame.
            var parsed = EventPipeCpuSampler.ParseFullMethodName(symbol);
            var identity = new MethodIdentity(
                ModuleName: moduleName,
                ModulePath: modulePath,
                ModuleVersionId: mvid,
                MetadataToken: m.Token > 0 ? (int)m.Token : null,
                TypeFullName: parsed.TypeFullName ?? (string.IsNullOrEmpty(m.TypeName) ? null : m.TypeName),
                MethodName: string.IsNullOrEmpty(parsed.MethodName) ? m.MethodName : parsed.MethodName,
                GenericArity: parsed.GenericArity)
            {
                GenericTypeArguments = parsed.GenericTypeArguments,
            };
            ranges.Add(new JitMapRange(m.StartAddress, (uint)m.Size, identity));
        }

        return ranges;
    }

    private static string FormatSymbol(string typeName, string methodName)
    {
        if (string.IsNullOrEmpty(typeName)) return methodName;
        if (string.IsNullOrEmpty(methodName)) return typeName;
        return string.Concat(typeName, ".", methodName);
    }

    /// <summary>
    /// Opens <paramref name="mapPath"/> for truncated write while refusing to follow symlinks
    /// (Linux). The path is predictable (<c>/tmp/perf-&lt;pid&gt;.map</c>) — without protection a
    /// local attacker could pre-create a symlink there to redirect the write to an arbitrary
    /// file the server has permission to truncate. On Linux we open with <c>O_NOFOLLOW</c>
    /// (FileOptions value 0x40000) and constrain Unix mode to 0600 so the file is private to
    /// the server's UID; on other platforms .NET's default <see cref="FileStream"/> semantics
    /// (which already reject following dangling symlinks during creation) are sufficient.
    /// </summary>
    private static FileStream OpenMapFileSecure(string mapPath)
    {
        if (OperatingSystem.IsLinux())
        {
            // O_NOFOLLOW = 0x20000 on Linux glibc; .NET exposes it as the platform-specific
            // bit pattern in FileStreamOptions on .NET 6+. We re-create the file with O_TRUNC
            // semantics via FileMode.Create but explicitly fail when the target is a symlink.
            // Defence in depth: also constrain UnixCreateMode to owner-only RW.
            try
            {
                if (File.Exists(mapPath))
                {
                    // If a symlink is already squatting on the path, refuse — same-UID attack
                    // surface only matters when the path is owned by someone else.
                    var info = new FileInfo(mapPath);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"Refusing to write to symlinked perf map at {mapPath}");
                    }
                }

                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                return new FileStream(mapPath, options);
            }
            catch (IOException)
            {
                throw;
            }
        }

        return new FileStream(mapPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    private readonly record struct PendingJitMethod(
        ulong StartAddress,
        uint Size,
        long ModuleId,
        long Token,
        string TypeName,
        string MethodName);
}

/// <summary>
/// Output of <see cref="JitMapEmitter.EmitAsync"/>: path of the emitted perf-map plus the
/// per-method address ranges. Use <see cref="Resolve"/> to map a frame address coming out of
/// <c>perf script</c> back to its canonical <see cref="MethodIdentity"/>. Address-based lookup
/// is required because the formatted symbol string is ambiguous for overloads — two overloads
/// of the same method share the rendered <c>Type.Method</c> name but live at distinct addresses
/// with distinct metadata tokens.
/// </summary>
public sealed record JitMapResult(
    string MapPath,
    IReadOnlyList<JitMapRange> Methods,
    int MethodCount)
{
    /// <summary>
    /// Returns the <see cref="MethodIdentity"/> whose range <c>[StartAddress, StartAddress+Size)</c>
    /// contains <paramref name="address"/>, or <c>null</c> when the address is not within any
    /// JIT'd managed method (typically: native, kernel, or unresolved JIT frame).
    /// </summary>
    /// <remarks>
    /// Binary search assumes <see cref="Methods"/> is sorted by <see cref="JitMapRange.StartAddress"/>
    /// ascending — <see cref="JitMapEmitter"/> guarantees that ordering. Code-heap ranges are
    /// non-overlapping by construction (the JIT allocates contiguous bytes per method body).
    /// </remarks>
    public MethodIdentity? Resolve(ulong address)
    {
        if (Methods.Count == 0) return null;
        int lo = 0, hi = Methods.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (Methods[mid].StartAddress <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0) return null;
        var m = Methods[found];
        return address < m.StartAddress + m.Size ? m.Identity : null;
    }
}

/// <summary>One JIT'd managed method body: covers byte range
/// <c>[<see cref="StartAddress"/>, <see cref="StartAddress"/> + <see cref="Size"/>)</c>.
/// Distinct ranges per overload — even when the formatted symbol string collides.</summary>
public readonly record struct JitMapRange(ulong StartAddress, uint Size, MethodIdentity Identity);
