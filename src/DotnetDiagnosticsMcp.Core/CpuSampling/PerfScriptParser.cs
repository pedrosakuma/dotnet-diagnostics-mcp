using System.Globalization;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Parser for the textual output of <c>perf script</c>. Each sample begins with a header
/// line (process / pid / timestamp / event) followed by one frame per indented line and
/// is terminated by a blank line. We only care about the indented stack frames here.
/// </summary>
/// <remarks>
/// Frame format (whitespace-separated):
/// <code>
///     ffffabcd Method+0x12 (/path/to/module.so)
/// </code>
/// or for kernel / anonymous frames:
/// <code>
///     ffffabcd [unknown] ([kernel.kallsyms])
/// </code>
/// The <c>Method</c> field may contain spaces inside <c>::operator()</c> etc; the module
/// is always the last parenthesised token on the line, so we parse from the right.
/// </remarks>
internal static class PerfScriptParser
{
    /// <summary>
    /// Parses textual <c>perf script</c> output into a sequence of stacks. Each stack is a
    /// list of frames ordered leaf→root (the same orientation <c>perf script</c> uses).
    /// </summary>
    /// <param name="output">Captured stdout of <c>perf script -i &lt;file&gt;</c>.</param>
    /// <param name="processId">When non-zero, frames from samples whose header reports a different
    /// pid are skipped. Matches the &quot;process 1234&quot; segment of the header line.</param>
    public static IReadOnlyList<PerfSample> Parse(string output, int processId = 0)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Optionally restrict to a single OS process. `perf record -p <pid>` records all
        // tasks (threads) of <pid>; the textual header that `perf script` produces, however,
        // reports the per-thread TID, NOT the parent PID. Filtering with samplePid != processId
        // therefore dropped every threadpool/GC/network worker sample and only kept frames that
        // happened to be running on the thread whose TID equals the PID (typically just the
        // main thread). To preserve correctness when the caller passes a non-zero processId
        // we compare against the set of TIDs currently belonging to that process — read once
        // at parse start from /proc/<pid>/task. When that lookup is unavailable (non-Linux,
        // or the process already exited), fall back to exact PID matching so replayed fixtures
        // behave consistently across OSes.
        HashSet<int>? acceptedTids = null;
        if (processId != 0)
        {
            acceptedTids = TryReadProcessTids(processId);
        }

        var samples = new List<PerfSample>();
        var lines = output.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var header = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(header))
            {
                i++;
                continue;
            }

            if (header.StartsWith('#'))
            {
                i++;
                continue;
            }

            // Header line is the sample preamble; the frames follow until a blank line.
            // Header doesn't start with whitespace; frames are indented.
            if (char.IsWhiteSpace(header[0]))
            {
                // Frame line without a preceding header — treat as orphan and skip.
                i++;
                continue;
            }

            var samplePid = TryExtractPid(header);
            if (samplePid != 0)
            {
                var shouldSkip = acceptedTids is { } tids
                    ? !tids.Contains(samplePid)
                    : processId != 0 && samplePid != processId;
                if (shouldSkip)
                {
                    // Skip this sample's frames.
                    i = SkipToBlank(lines, i + 1);
                    continue;
                }
            }

            var frames = new List<PerfFrame>();
            i++;
            while (i < lines.Length)
            {
                var line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) break;
                if (!char.IsWhiteSpace(line[0])) break;

                var frame = ParseFrame(line);
                if (frame is not null) frames.Add(frame);
                i++;
            }

            if (frames.Count > 0) samples.Add(new PerfSample(samplePid, frames));
        }

        return samples;
    }

    private static int SkipToBlank(string[] lines, int start)
    {
        for (var k = start; k < lines.Length; k++)
        {
            if (string.IsNullOrWhiteSpace(lines[k])) return k + 1;
        }
        return lines.Length;
    }

    private static readonly char[] HeaderSeparators = new[] { ' ', '\t' };

    private static HashSet<int>? TryReadProcessTids(int processId)
    {
        try
        {
            var taskDir = $"/proc/{processId}/task";
            if (!Directory.Exists(taskDir)) return null;
            var set = new HashSet<int>();
            foreach (var dir in Directory.EnumerateDirectories(taskDir))
            {
                var name = Path.GetFileName(dir);
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                {
                    set.Add(tid);
                }
            }

            // Always accept the PID itself even if the task directory was racing.
            set.Add(processId);
            return set;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryExtractPid(string header)
    {
        // perf-script default header has the form:
        //   sample-target  1234 [001]  12345.678901:    cpu-clock:
        // The PID is the second whitespace-delimited token. The first token (the command
        // name) may itself contain spaces but is left-padded to a fixed width, so the PID
        // is always the first token that parses as int.
        foreach (var token in header.Split(HeaderSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return 0;
    }

    private static PerfFrame? ParseFrame(string line)
    {
        // Indented frame: "    <hex address> <symbol+offset> (<module>)"
        // The module is the last parenthesised token; the symbol is everything between
        // the first non-whitespace, non-address token and the opening paren of the module.
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return null;

        var lastOpen = trimmed.LastIndexOf('(');
        var lastClose = trimmed.LastIndexOf(')');
        string module;
        string symbolPart;
        if (lastOpen >= 0 && lastClose > lastOpen)
        {
            module = trimmed.Substring(lastOpen + 1, lastClose - lastOpen - 1);
            symbolPart = trimmed[..lastOpen].TrimEnd();
        }
        else
        {
            module = string.Empty;
            symbolPart = trimmed;
        }

        // Drop the leading hex address token.
        var firstSpace = symbolPart.IndexOf(' ');
        var symbol = firstSpace > 0 ? symbolPart[(firstSpace + 1)..].TrimStart() : symbolPart;

        // Strip the trailing "+0x..." offset to make the symbol stable across samples
        // at different instruction offsets within the same function.
        var plus = symbol.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0) symbol = symbol[..plus];

        if (symbol.Length == 0) return null;
        return new PerfFrame(Module: module, Symbol: symbol);
    }
}

internal sealed record PerfSample(int ProcessId, IReadOnlyList<PerfFrame> Frames);

internal sealed record PerfFrame(string Module, string Symbol);
