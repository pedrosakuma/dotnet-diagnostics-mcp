using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.2 / issue #210 — dual-entrypoint contract tests for
/// <see cref="CollectSampleTool"/>. For every supported <c>kind</c>, asserts that calling
/// <c>collect_sample(kind="X", ...)</c> produces an envelope structurally equivalent to the
/// envelope produced by the legacy collector tool (<c>collect_cpu_sample</c>,
/// <c>collect_off_cpu_sample</c>, <c>collect_allocation_sample</c>). The polymorphic payload
/// field for the chosen kind is compared against the legacy payload directly; the surrounding
/// envelope (Summary, Hints, ResolvedProcess) is sanity-checked rather than diffed
/// byte-for-byte because the legacy tools carry per-tool next-action hints that the
/// discriminator entry-point inherits verbatim.
/// </summary>
/// <remarks>
/// <para>The tests exercise the test host process itself (<see cref="Environment.ProcessId"/>) —
/// same pattern as the existing sampler tests in <c>McpToolsTests</c>. The off-CPU test is
/// skipped on non-Linux hosts where perf-based sched_switch capture is unsupported (matches
/// the runtime gate inside <see cref="DiagnosticTools.CollectOffCpuSample"/>).</para>
/// </remarks>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class CollectSampleCompatibilityTests : IClassFixture<CollectSampleCompatibilityTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public CollectSampleCompatibilityTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Kind_Cpu_MatchesLegacyCollectCpuSample()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 5,
            ["resolveSourceLines"] = false,
        };

        var legacy = DeserializeStructured<CpuSample>(
            await client.CallToolAsync("collect_cpu_sample", common, cancellationToken: CancellationToken.None));
        var unified = DeserializeStructured<CollectSampleEnvelope>(
            await client.CallToolAsync("collect_sample", With(common, ("kind", "cpu")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("cpu");
        unified.Cpu.Should().NotBeNull();
        unified.OffCpu.Should().BeNull();
        unified.Allocation.Should().BeNull();

        unified.Cpu!.ProcessId.Should().Be(legacy!.ProcessId);

        // Field-stripping guard: every public property the legacy payload populated must also be
        // populated on the unified side. Counts of variable-content collections (TopHotspots,
        // ResolvedFrames, …) may legitimately differ run-to-run, so we only assert presence /
        // non-null parity here rather than byte-equality. Catches regressions where the
        // discriminator wrapper accidentally drops a sub-record.
        AssertSameShape(legacy, unified.Cpu);
    }

    [Fact]
    public async Task Kind_Cpu_RunAsJob_PreservesLegacyJobAckShape()
    {
        // Issue #210 / reviewer note: with runAsJob=true the legacy CollectCpuSample returns a
        // success envelope whose Data is null and whose Handle carries the job id. The unified
        // tool must preserve that shape exactly — not wrap null as `{ kind:"cpu", cpu:null, … }`,
        // which would silently change the ack JSON for every existing async-collection client.
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 5,
            ["resolveSourceLines"] = false,
            ["runAsJob"] = true,
        };

        var legacyResult = await client.CallToolAsync("collect_cpu_sample", common, cancellationToken: CancellationToken.None);
        var unifiedResult = await client.CallToolAsync("collect_sample", With(common, ("kind", "cpu")), cancellationToken: CancellationToken.None);

        var legacy = DeserializeEnvelope(legacyResult)!;
        var unified = DeserializeEnvelope(unifiedResult)!;

        legacy.Error.Should().BeNull();
        unified.Error.Should().BeNull();
        legacy.Handle.Should().NotBeNullOrEmpty("legacy runAsJob ack carries the job handle");
        unified.Handle.Should().NotBeNullOrEmpty("unified runAsJob ack must carry the same handle field");

        // The job-ack Data must be null on BOTH legacy and unified — wrapping it as an empty
        // CollectSampleEnvelope would violate the byte-equivalence contract.
        IsJsonNullOrAbsent(legacy.Data).Should().BeTrue("legacy runAsJob ack has Data=null");
        IsJsonNullOrAbsent(unified.Data).Should().BeTrue("unified runAsJob ack must keep Data=null, not wrap it as an empty envelope");
    }

    private static bool IsJsonNullOrAbsent(JsonElement? element)
        => element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    [Fact]
    public async Task Kind_Allocation_MatchesLegacyCollectAllocationSample()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 10,
        };

        // GCAllocationTick fires every ~100KB of managed allocations. Without explicit
        // pressure the test host may produce zero ticks in one of the two 2s windows
        // (causing TopByBytes non-emptiness to differ between legacy and unified runs).
        // Drive a steady ~5MB/s allocation rate from a background task that spans both
        // sample windows; cancel it after both calls complete.
        using var pressureCts = new CancellationTokenSource();
        var pressureTask = Task.Run(async () =>
        {
            var rng = new Random(42);
            while (!pressureCts.IsCancellationRequested)
            {
                _ = new byte[64 * 1024];
                _ = rng.Next();
                await Task.Delay(10, pressureCts.Token).ConfigureAwait(false);
            }
        }, pressureCts.Token);

        try
        {
            var legacy = DeserializeStructured<AllocationSample>(
                await client.CallToolAsync("collect_allocation_sample", common, cancellationToken: CancellationToken.None));
            var unified = DeserializeStructured<CollectSampleEnvelope>(
                await client.CallToolAsync("collect_sample", With(common, ("kind", "allocation")), cancellationToken: CancellationToken.None));

            unified!.Kind.Should().Be("allocation");
            unified.Allocation.Should().NotBeNull();
            unified.Cpu.Should().BeNull();
            unified.OffCpu.Should().BeNull();

            unified.Allocation!.ProcessId.Should().Be(legacy!.ProcessId);
            unified.Allocation.TotalEvents.Should().BeGreaterThanOrEqualTo(0);
            AssertSameShape(legacy, unified.Allocation);
        }
        finally
        {
            pressureCts.Cancel();
            try { await pressureTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Kind_OffCpu_MatchesLegacyCollectOffCpuSample_OrSurfacesUnsupported()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 5,
        };

        var legacyResult = await client.CallToolAsync("collect_off_cpu_sample", common, cancellationToken: CancellationToken.None);
        var unifiedResult = await client.CallToolAsync("collect_sample", With(common, ("kind", "off_cpu")), cancellationToken: CancellationToken.None);

        // Both must report the same posture: on platforms without a usable backend (or in CI
        // sandboxes without CAP_PERFMON) the legacy tool returns a NotSupported / PermissionDenied
        // envelope. The unified tool must surface the exact same error kind.
        var legacyEnv = DeserializeEnvelope(legacyResult);
        var unifiedEnv = DeserializeEnvelope(unifiedResult);

        if (legacyEnv?.Error is not null)
        {
            unifiedEnv!.Error.Should().NotBeNull("unified tool must report the same NotSupported/PermissionDenied posture as the legacy tool");
            unifiedEnv.Error!.Kind.Should().Be(legacyEnv.Error.Kind);
            return;
        }

        // Both succeeded — verify the payload routes onto the OffCpu slot.
        var legacy = DeserializeStructured<OffCpuSnapshot>(legacyResult);
        var unified = DeserializeStructured<CollectSampleEnvelope>(unifiedResult);

        unified!.Kind.Should().Be("off_cpu");
        unified.OffCpu.Should().NotBeNull();
        unified.Cpu.Should().BeNull();
        unified.Allocation.Should().BeNull();
        unified.OffCpu!.ProcessId.Should().Be(legacy!.ProcessId);
        AssertSameShape(legacy, unified.OffCpu);
    }

    [Fact]
    public async Task Kind_Invalid_ReturnsInvalidArgumentListingAllowedKinds()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "collect_sample",
            new Dictionary<string, object?>
            {
                ["kind"] = "not-a-real-kind",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        envelope.Error.Message.Should().Contain("cpu")
            .And.Contain("off_cpu")
            .And.Contain("allocation");
    }

    [Fact]
    public async Task LegacyTools_RetainOriginalNames_AndCarryDeprecationNotice()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        foreach (var legacy in new[] { "collect_cpu_sample", "collect_off_cpu_sample", "collect_allocation_sample" })
        {
            var tool = tools.SingleOrDefault(t => t.Name == legacy);
            tool.Should().NotBeNull($"legacy tool '{legacy}' must remain registered through the deprecation window");
            tool!.Description.Should().Contain("DEPRECATED")
                .And.Contain("collect_sample", $"legacy tool '{legacy}' must point callers at collect_sample");
        }

        tools.Should().Contain(t => t.Name == "collect_sample");
    }

    /// <summary>
    /// Structural-parity check between the legacy payload and the unified payload routed onto
    /// the same kind slot. Sampling data necessarily differs between two independent collection
    /// runs (different threads scheduled, different GCAllocationTick fires, …) so we cannot
    /// byte-compare values. Instead we walk the public properties and assert that every
    /// property the legacy payload populated to a non-null/non-empty value is also populated on
    /// the unified payload, and that collection-typed properties match in non-emptiness. This is
    /// the strongest run-to-run check available and catches the failure modes the consolidation
    /// could plausibly introduce: dropped sub-records, stripped collections, or null-projection
    /// regressions in <see cref="CollectSampleTool"/>.
    /// </summary>
    private static void AssertSameShape<T>(T legacy, T unified) where T : class
    {
        legacy.Should().NotBeNull();
        unified.Should().NotBeNull();

        foreach (var prop in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var l = prop.GetValue(legacy);
            var u = prop.GetValue(unified);

            if (l is null) continue;

            u.Should().NotBeNull($"property '{prop.Name}' was populated by the legacy collector but is null on the unified payload (field-stripping regression)");

            if (l is System.Collections.IEnumerable lEnum and not string)
            {
                var lAny = lEnum.Cast<object?>().Any();
                var uAny = ((System.Collections.IEnumerable)u!).Cast<object?>().Any();
                // Both runs must agree on whether the collection produced any items. Run-to-run
                // jitter means the contents will differ, but a regression that strips the field
                // would surface as "legacy non-empty / unified empty".
                uAny.Should().Be(lAny, $"property '{prop.Name}' non-emptiness must match between legacy and unified payloads");
            }
        }
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> source, params (string Key, object? Value)[] overrides)
    {
        var copy = new Dictionary<string, object?>(source);
        foreach (var (k, v) in overrides) copy[k] = v;
        return copy;
    }

    private async Task<McpClient> ConnectAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AuthedFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {AuthedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, clientOptions: null, cancellationToken: CancellationToken.None);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static T? DeserializeStructured<T>(CallToolResult result)
    {
        result.IsError.Should().NotBe(true, "tool call must succeed");
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : result.Content.OfType<TextContentBlock>().First().Text;
        var envelope = JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull();
        envelope!.Summary.Should().NotBeNullOrWhiteSpace();
        envelope.Error.Should().BeNull();
        return envelope.Data;
    }

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(CallToolResult result)
    {
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : result.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
    }

    public sealed class AuthedFactory : WebApplicationFactory<DotnetDiagnosticsMcp.Server.Program>
    {
        public const string Token = "test-bearer-collect-sample-do-not-use";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
