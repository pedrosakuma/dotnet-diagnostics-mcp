using System.Globalization;
using System.Text;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Best-effort pretty-printer for NativeAOT-mangled ELF symbol names emitted by the
/// <c>ilc</c> compiler (issue #29). When a NativeAOT app is published with
/// <c>&lt;StripSymbols&gt;false&lt;/StripSymbols&gt;</c> (the diagnostics-friendly opt-in),
/// <c>perf</c> reports stack frames using the mangled symbol name embedded in the ELF
/// <c>.symtab</c>. Those strings are accurate but long and hard to read — e.g.
/// <c>Microsoft_AspNetCore_Http_Microsoft_AspNetCore_Internal_AdaptiveCapacityDictionary_2_Enumerator__MoveNext</c>.
/// This class turns them into something closer to a managed display name.
/// </summary>
/// <remarks>
/// <para>
/// The NativeAOT name mangling rules (per <c>NameMangler</c> in dotnet/runtime) double
/// every literal <c>_</c> in a managed identifier so the resulting string still uses single
/// underscores as the separator between mangled segments. We cannot perfectly invert that
/// (some compiler-generated names like <c>&lt;Name&gt;b__0_1</c> get further mangled to
/// <c>_Name_b_0_1</c> with no way to tell ambiguous splits apart), so the demangler is a
/// best-effort cleanup, not a full inverse. The original mangled name is always preserved
/// next to the demangled form for debugging.
/// </para>
/// <para>Recognised shapes (verified against an unstripped Linux NativeAOT publish):</para>
/// <list type="bullet">
///   <item><description><c>S_P_CoreLib_System_Foo_Bar__Method</c> — System.Private.CoreLib types.</description></item>
///   <item><description><c>Microsoft_AspNetCore_Http_Microsoft_AspNetCore_Http_Type__Method</c> — assembly + namespace + type + method.</description></item>
///   <item><description><c>&lt;Boxed&gt;X__&lt;unbox&gt;X__Method</c> — boxed unbox stubs.</description></item>
///   <item><description><c>unbox_X__Method</c> — explicit unbox shims.</description></item>
///   <item><description><c>X&lt;T1__T2&gt;__Method&lt;T3&gt;</c> — generics use <c>&lt;...&gt;</c> with <c>__</c> as the inner-arg separator.</description></item>
/// </list>
/// </remarks>
public static class NativeAotSymbolDemangler
{
    /// <summary>
    /// Returns a human-readable display name for a NativeAOT mangled symbol. When the
    /// symbol does not match any of the known shapes the original string is returned
    /// unchanged. Always idempotent: passing an already-demangled name (or any non-mangled
    /// symbol like a C P/Invoke) yields the same input.
    /// </summary>
    /// <param name="mangled">Raw symbol as captured by <c>perf script</c> or <c>nm</c>.
    /// May be <c>null</c> or empty — returns the input as-is in that case.</param>
    public static string Demangle(string? mangled)
    {
        if (string.IsNullOrEmpty(mangled)) return mangled ?? string.Empty;

        // Positive evidence gate (fix for #29 review #1 + #2): only rewrite strings that
        // actually look like NativeAOT-mangled managed names. Everything else — already-
        // demangled strings, C/P-Invoke names like pthread_mutex_lock, libc, kernel — is
        // returned unchanged so Demangle is safely idempotent on its own output.
        if (!LooksLikeNativeAotMangled(mangled)) return mangled;

        // Boxed unbox stubs: "<Boxed>X__<unbox>Y__M". When X == Y the stub forwards directly
        // and we collapse to "X.M (boxed)". When they differ (explicit interface impls) we
        // preserve both halves so the call site stays unambiguous.
        if (mangled.StartsWith("<Boxed>", StringComparison.Ordinal))
        {
            var unboxIdx = mangled.IndexOf("__<unbox>", StringComparison.Ordinal);
            if (unboxIdx > 0)
            {
                var boxedHalf = mangled["<Boxed>".Length..unboxIdx];
                var tail = mangled[(unboxIdx + "__<unbox>".Length)..];
                var tailFirstDouble = IndexOfDoubleUnderscore(tail, 0);
                var unboxTypeHalf = tailFirstDouble >= 0 ? tail[..tailFirstDouble] : tail;
                if (!string.Equals(boxedHalf, unboxTypeHalf, StringComparison.Ordinal))
                {
                    return Demangle(boxedHalf) + " → " + Demangle(tail) + " (boxed)";
                }
                return Demangle(tail) + " (boxed)";
            }
        }

        if (mangled.StartsWith("unbox_", StringComparison.Ordinal))
        {
            return Demangle(mangled["unbox_".Length..]) + " (unbox)";
        }

        // S_P_CoreLib_X_Y__Method → System.Private.CoreLib + X.Y.Method.
        if (mangled.StartsWith("S_P_CoreLib_", StringComparison.Ordinal))
        {
            return "System.Private.CoreLib." + DemangleCore(mangled["S_P_CoreLib_".Length..]);
        }

        return DemangleCore(mangled);
    }

    private static string DemangleCore(string mangled)
    {
        // Split off generic arguments first: outermost balanced <...> groups become
        // ", "-joined demangled recursive calls.
        if (mangled.IndexOf('<') < 0)
        {
            return PrettifyDottedSegments(mangled);
        }

        var sb = new StringBuilder(mangled.Length);
        var i = 0;
        var lastPlainStart = 0;
        while (i < mangled.Length)
        {
            var c = mangled[i];
            if (c == '<')
            {
                // Flush the plain run up to here.
                sb.Append(PrettifyDottedSegments(mangled[lastPlainStart..i]));
                var depth = 1;
                var inner = new StringBuilder();
                i++;
                while (i < mangled.Length && depth > 0)
                {
                    var ch = mangled[i];
                    if (ch == '<') depth++;
                    else if (ch == '>') { depth--; if (depth == 0) break; }
                    inner.Append(ch);
                    i++;
                }
                sb.Append('<');
                sb.Append(SplitTypeArgs(inner.ToString()));
                sb.Append('>');
                i++; // consume closing '>'
                lastPlainStart = i;
            }
            else
            {
                i++;
            }
        }
        sb.Append(PrettifyDottedSegments(mangled[lastPlainStart..]));
        return sb.ToString();
    }

    private static string SplitTypeArgs(string inner)
    {
        // Generic args are joined with "__" at the same nesting level.
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (depth == 0 && i + 1 < inner.Length && c == '_' && inner[i + 1] == '_')
            {
                parts.Add(inner[start..i]);
                i++;            // step over second '_'
                start = i + 1;  // skip the second '_' on next iter
            }
        }
        parts.Add(inner[start..]);
        return string.Join(", ", parts.Select(Demangle));
    }

    private static string PrettifyDottedSegments(string segment)
    {
        if (segment.Length == 0) return segment;

        // Promote the FIRST "__" (if any) to a separator between type FQN and method name.
        // Everything before becomes "Type.Sub.Sub", everything after stays joined with "_"
        // because compiler-generated locals (b__0_1, d__7) need their internal underscores.
        var firstDouble = IndexOfDoubleUnderscore(segment, 0);
        if (firstDouble < 0)
        {
            return ConvertSegmentUnderscoresToDots(segment);
        }

        var typePart = segment[..firstDouble];
        var afterFirst = segment[(firstDouble + 2)..];

        // A SECOND "__" downstream typically marks "method__overloadOrInterfaceImpl" or a
        // canon-instantiation suffix; keep it but render as " · " separator so the LLM still
        // sees the boundary without us guessing wrong about which dot to insert.
        var secondDouble = IndexOfDoubleUnderscore(afterFirst, 0);
        string methodPart;
        string? trailing = null;
        if (secondDouble >= 0)
        {
            methodPart = afterFirst[..secondDouble];
            trailing = afterFirst[(secondDouble + 2)..];
        }
        else
        {
            methodPart = afterFirst;
        }

        var fqType = ConvertSegmentUnderscoresToDots(typePart);
        return trailing is null
            ? fqType + "." + methodPart
            : fqType + "." + methodPart + " [" + trailing + "]";
    }

    private static int IndexOfDoubleUnderscore(string s, int start)
    {
        for (var i = start; i + 1 < s.Length; i++)
        {
            if (s[i] == '_' && s[i + 1] == '_') return i;
        }
        return -1;
    }

    private static string ConvertSegmentUnderscoresToDots(string segment)
    {
        // Single '_' is the segment separator in the mangled form; literal '_' chars from
        // source identifiers were doubled. After IndexOfDoubleUnderscore has consumed those
        // boundaries we can safely treat remaining '_' as separators. Trailing arity tags
        // like "_2" on generic types are preserved as `2 (managed convention).
        var parts = segment.Split('_');
        var sb = new StringBuilder(segment.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            if (sb.Length > 0)
            {
                // Numeric segment immediately following an identifier is the generic arity:
                // "Dictionary_2" → "Dictionary`2".
                if (IsAllDigits(parts[i]))
                {
                    sb.Append('`').Append(parts[i]);
                    continue;
                }
                sb.Append('.');
            }
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    private static bool IsAllDigits(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }
        return s.Length > 0;
    }

    /// <summary>Recognises well-known C / loader / kernel symbols that should always
    /// pass through Demangle unchanged regardless of underscore content.</summary>
    private static bool IsKnownNativeSymbol(string s)
    {
        if (s.StartsWith("CryptoNative_", StringComparison.Ordinal) ||
            s.StartsWith("SystemNative_", StringComparison.Ordinal) ||
            s.StartsWith("GlobalizationNative_", StringComparison.Ordinal) ||
            s.StartsWith("CompressionNative_", StringComparison.Ordinal) ||
            s.StartsWith("HttpNative_", StringComparison.Ordinal) ||
            s.StartsWith("NetSecurityNative_", StringComparison.Ordinal))
        {
            return true;
        }

        if (s.StartsWith("__libc_", StringComparison.Ordinal) ||
            s.StartsWith("__GI_", StringComparison.Ordinal) ||
            s.StartsWith("[k", StringComparison.Ordinal) ||
            s == "[unknown]")
        {
            return true;
        }

        // C++ ItaniumABI manglings — not managed.
        if (s.StartsWith("_Z", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>True when the symbol carries positive evidence of NativeAOT mangling. Names
    /// without these markers (e.g. <c>pthread_mutex_lock</c>, <c>clock_gettime</c>,
    /// or an already-demangled <c>HeaderDictionary.get_ContentLength</c>) are not rewritten
    /// — they pass through Demangle unchanged.</summary>
    private static bool LooksLikeNativeAotMangled(string s)
    {
        if (IsKnownNativeSymbol(s)) return false;
        return s.StartsWith("S_P_", StringComparison.Ordinal)
            || s.StartsWith("<Boxed>", StringComparison.Ordinal)
            || s.StartsWith("unbox_", StringComparison.Ordinal)
            || s.Contains("__", StringComparison.Ordinal);
    }

    /// <summary>Provenance of a frame's display name. Threaded into <c>CpuSampleTraceArtifact</c>
    /// so the LLM knows whether to trust the name as-is or treat it as a heuristic guess.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SymbolSource>))]
    public enum SymbolSource
    {
        Unknown = 0,
        /// <summary>Came from perf's ELF symbol resolution and looked managed-mangled.</summary>
        ElfMangled,
        /// <summary>Same as ElfMangled but ran through <see cref="Demangle"/>.</summary>
        ElfDemangled,
        /// <summary>Came from perf for a non-managed (libc / P/Invoke / kernel) frame.</summary>
        Native,
        /// <summary>Synthetic / stripped — perf returned <c>[unknown]</c> or an address.</summary>
        Stripped,
        /// <summary>Trace mixed multiple distinct sources (e.g. some frames stripped, others
        /// demangled). The LLM should not assume uniform quality across the trace.</summary>
        Mixed,
    }

    /// <summary>Classifies a perf-emitted symbol so the artifact can carry a coarse source
    /// label without retaining per-frame metadata.</summary>
    public static SymbolSource Classify(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return SymbolSource.Stripped;
        if (symbol == "[unknown]" || symbol.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return SymbolSource.Stripped;
        if (IsKnownNativeSymbol(symbol)) return SymbolSource.Native;
        if (LooksLikeNativeAotMangled(symbol)) return SymbolSource.ElfMangled;
        // No NativeAOT markers and not a known native prefix: a name with '.' looks like an
        // already-demangled managed display string; otherwise treat as a C symbol.
        if (symbol.Contains('.', StringComparison.Ordinal)) return SymbolSource.ElfDemangled;
        return SymbolSource.Native;
    }

    /// <summary>Combines two source labels for trace-level rollup. Identical labels collapse;
    /// ElfMangled+ElfDemangled both indicate managed names and collapse to ElfDemangled (the
    /// artifact ships demangled strings); any other combination of distinct concrete sources
    /// produces <see cref="SymbolSource.Mixed"/> so the consumer knows quality is not uniform.</summary>
    public static SymbolSource Combine(SymbolSource a, SymbolSource b)
    {
        if (a == SymbolSource.Unknown) return b;
        if (b == SymbolSource.Unknown) return a;
        if (a == b) return a;
        if ((a == SymbolSource.ElfMangled && b == SymbolSource.ElfDemangled) ||
            (a == SymbolSource.ElfDemangled && b == SymbolSource.ElfMangled))
        {
            return SymbolSource.ElfDemangled;
        }
        return SymbolSource.Mixed;
    }

    // Reserved for future use: surface culture-invariant numeric formatting helpers.
    internal static string FormatHex(long value) => value.ToString("x", CultureInfo.InvariantCulture);
}
