using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Best-effort post-sample enrichment that re-attaches via ClrMD and resolves closed generic
/// instantiations for the hottest managed frames by instruction pointer. This is opt-in because
/// live attach briefly suspends the target and requires ptrace / debug-attach privileges.
/// </summary>
public sealed class ClrMdMethodInstantiationEnricher
{
    private readonly ILogger<ClrMdMethodInstantiationEnricher> _logger;
    private readonly MvidReader _mvidReader;

    public ClrMdMethodInstantiationEnricher(
        ILogger<ClrMdMethodInstantiationEnricher>? logger = null,
        MvidReader? mvidReader = null)
    {
        _logger = logger ?? NullLogger<ClrMdMethodInstantiationEnricher>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
    }

    internal IReadOnlyList<ResolvedMethodInstantiation> Resolve(
        int processId,
        IReadOnlyList<MethodInstantiationCandidate> candidates,
        IReadOnlyDictionary<SymbolRef, MethodIdentity> identities,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || identities.Count == 0)
        {
            return Array.Empty<ResolvedMethodInstantiation>();
        }

        var resolved = new List<ResolvedMethodInstantiation>(candidates.Count);

        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime (NativeAOT or non-managed).");
        using var runtime = clrInfo.CreateRuntime();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!identities.TryGetValue(candidate.Symbol, out var identity))
            {
                continue;
            }

            if (identity.MetadataToken is not int token || candidate.InstructionPointer == 0)
            {
                continue;
            }

            var method = runtime.GetMethodByInstructionPointer(candidate.InstructionPointer);
            if (method is null)
            {
                continue;
            }

            if (method.MetadataToken != token)
            {
                _logger.LogDebug(
                    "Skipping generic enrichment for {Symbol}: ClrMD token 0x{ClrToken:X8} != trace token 0x{TraceToken:X8}.",
                    candidate.Symbol,
                    method.MetadataToken,
                    token);
                continue;
            }

            var clrModulePath = method.Type?.Module?.Name;
            var clrMvid = _mvidReader.TryRead(clrModulePath);
            if (identity.ModuleVersionId is Guid expectedMvid && clrMvid is Guid actualMvid && expectedMvid != actualMvid)
            {
                _logger.LogDebug(
                    "Skipping generic enrichment for {Symbol}: ClrMD mvid {ActualMvid} != trace mvid {ExpectedMvid}.",
                    candidate.Symbol,
                    actualMvid,
                    expectedMvid);
                continue;
            }

            var parsed = ParseClosedSignature(method.Signature);
            if (parsed.GenericTypeArguments is null || string.IsNullOrWhiteSpace(parsed.ClosedSignature))
            {
                continue;
            }

            var merged = Merge(identity.GenericTypeArguments, parsed.GenericTypeArguments);
            var genericArity = parsed.GenericArity > 0 ? parsed.GenericArity : identity.GenericArity;
            var closedSymbol = new SymbolRef(candidate.Symbol.Module, parsed.ClosedSignature);
            var closedIdentity = identity with
            {
                GenericArity = genericArity,
                GenericTypeArguments = merged,
                ClosedSignature = parsed.ClosedSignature,
            };
            resolved.Add(new ResolvedMethodInstantiation(candidate, closedSymbol, closedIdentity));
        }

        return resolved;
    }

    private static GenericInstantiation? Merge(GenericInstantiation? existing, GenericInstantiation parsed)
    {
        var typeArgs = parsed.Type.Count > 0 ? parsed.Type : existing?.Type ?? Array.Empty<string>();
        var methodArgs = parsed.Method.Count > 0 ? parsed.Method : existing?.Method ?? Array.Empty<string>();
        return typeArgs.Count == 0 && methodArgs.Count == 0
            ? null
            : new GenericInstantiation(typeArgs, methodArgs);
    }

    internal static ParsedClosedSignature ParseClosedSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return new ParsedClosedSignature(null, string.Empty, 0, null, null);
        }

        var trimmed = StripTrailingParameterSignature(signature.Trim());
        var lastDot = FindLastTopLevelDot(trimmed);
        if (lastDot <= 0 || lastDot == trimmed.Length - 1)
        {
            return new ParsedClosedSignature(null, trimmed, 0, null, null);
        }

        var rawType = trimmed[..lastDot];
        var rawMethod = trimmed[(lastDot + 1)..];
        var methodName = rawMethod;
        var methodArgs = Array.Empty<string>();

        var methodGenericOpen = FindFirstTopLevelBracket(rawMethod);
        if (methodGenericOpen > 0 && rawMethod[^1] == ']')
        {
            var methodGenericClose = FindMatchingBracket(rawMethod, methodGenericOpen);
            if (methodGenericClose == rawMethod.Length - 1)
            {
                methodName = rawMethod[..methodGenericOpen];
                methodArgs = ParseGenericArguments(rawMethod[(methodGenericOpen + 1)..methodGenericClose])
                    .Select(NormalizeClrMdTypeName)
                    .Where(arg => arg.Length > 0)
                    .ToArray();
            }
        }

        var normalizedType = NormalizeClrMdTypeName(rawType);
        var normalizedMethod = methodArgs.Length == 0
            ? methodName
            : $"{methodName}<{string.Join(",", methodArgs)}>";
        var synthetic = $"{normalizedType}.{normalizedMethod}";
        var parsed = EventPipeCpuSampler.ParseFullMethodName(synthetic);
        return new ParsedClosedSignature(
            parsed.TypeFullName,
            parsed.MethodName,
            parsed.GenericArity,
            parsed.GenericTypeArguments,
            parsed.GenericTypeArguments is null ? null : synthetic);
    }

    internal static string NormalizeClrMdTypeName(string? rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
        {
            return string.Empty;
        }

        var trimmed = RemoveTopLevelAssemblyQualification(rawTypeName.Trim());
        var open = FindFirstTopLevelBracket(trimmed);
        if (open < 0)
        {
            return trimmed;
        }

        var close = FindMatchingBracket(trimmed, open);
        if (close < 0)
        {
            return trimmed;
        }

        var prefix = trimmed[..open];
        var content = trimmed[(open + 1)..close];
        if (!prefix.Contains('`') && !content.StartsWith('['))
        {
            return trimmed;
        }

        var args = ParseGenericArguments(content)
            .Select(NormalizeClrMdTypeName)
            .Where(arg => arg.Length > 0)
            .ToArray();
        var suffix = trimmed[(close + 1)..];
        return args.Length == 0
            ? trimmed
            : $"{prefix}[{string.Join(",", args)}]{suffix}";
    }

    internal static string[] ParseGenericArguments(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var args = new List<string>();
        var span = content.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && (span[i] == ',' || char.IsWhiteSpace(span[i])))
            {
                i++;
            }

            if (i >= span.Length)
            {
                break;
            }

            if (span[i] == '[')
            {
                var start = i + 1;
                var depth = 1;
                i++;
                while (i < span.Length && depth > 0)
                {
                    if (span[i] == '[')
                    {
                        depth++;
                    }
                    else if (span[i] == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }

                    i++;
                }

                if (depth == 0)
                {
                    args.Add(content.Substring(start, i - start));
                    i++;
                    continue;
                }

                break;
            }

            var argStart = i;
            var bracketDepth = 0;
            while (i < span.Length)
            {
                if (span[i] == '[')
                {
                    bracketDepth++;
                }
                else if (span[i] == ']')
                {
                    bracketDepth--;
                }
                else if (span[i] == ',' && bracketDepth == 0)
                {
                    break;
                }

                i++;
            }

            args.Add(content.Substring(argStart, i - argStart));
        }

        return args.Select(arg => arg.Trim()).Where(arg => arg.Length > 0).ToArray();
    }

    private static string RemoveTopLevelAssemblyQualification(string value)
    {
        var squareDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth--;
                    break;
                case ',' when squareDepth == 0:
                    return value[..i].TrimEnd();
            }
        }

        return value;
    }

    private static int FindFirstTopLevelBracket(string value)
    {
        var squareDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[' when squareDepth == 0:
                    return i;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth--;
                    break;
            }
        }

        return -1;
    }

    private static int FindMatchingBracket(string value, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < value.Length; i++)
        {
            if (value[i] == '[')
            {
                depth++;
            }
            else if (value[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindLastTopLevelDot(string value)
    {
        var squareDepth = 0;
        var angleDepth = 0;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            switch (value[i])
            {
                case ']':
                    squareDepth++;
                    break;
                case '[':
                    squareDepth--;
                    break;
                case '>':
                    angleDepth++;
                    break;
                case '<':
                    angleDepth--;
                    break;
                case '.' when squareDepth == 0 && angleDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static string StripTrailingParameterSignature(string value)
    {
        if (value.Length == 0 || value[^1] != ')')
        {
            return value;
        }

        var parenDepth = 0;
        var squareDepth = 0;
        var angleDepth = 0;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            switch (value[i])
            {
                case ']':
                    squareDepth++;
                    break;
                case '[':
                    squareDepth--;
                    break;
                case '>':
                    angleDepth++;
                    break;
                case '<':
                    angleDepth--;
                    break;
                case ')' when squareDepth == 0 && angleDepth == 0:
                    parenDepth++;
                    break;
                case '(' when squareDepth == 0 && angleDepth == 0:
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        return value[..i];
                    }

                    break;
            }
        }

        return value;
    }
}

internal sealed record MethodInstantiationCandidate(SymbolRef Symbol, ulong InstructionPointer);
internal sealed record ResolvedMethodInstantiation(
    MethodInstantiationCandidate Candidate,
    SymbolRef ClosedSymbol,
    MethodIdentity Identity);

internal readonly record struct ParsedClosedSignature(
    string? TypeFullName,
    string MethodName,
    int GenericArity,
    GenericInstantiation? GenericTypeArguments,
    string? ClosedSignature);
