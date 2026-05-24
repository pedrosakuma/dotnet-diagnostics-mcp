using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 / #209 — dual-entrypoint compatibility coverage for the bootstrap consolidation.
/// For every supported <c>view</c>, calls the legacy tool AND the new
/// <c>inspect_process(view=…)</c> and asserts the two envelopes are structurally equal under
/// <see cref="CompatibilityEnvelopeAssert"/>. Provides the regression contract for the
/// deprecation window — a follow-up that accidentally diverges the successor from the legacy
/// shape fails here before it can reach a client.
/// </summary>
/// <remarks>
/// <para>The successor returns <see cref="DotnetDiagnosticsMcp.Server.Tools.InspectProcessReport"/>
/// wrapping the legacy payload under one of <c>list</c> / <c>info</c> / <c>capabilities</c> /
/// <c>container</c> / <c>memoryTrend</c>. The test extracts the matching field and compares
/// it against the legacy envelope's <c>data</c> field — the rest of the envelope
/// (<c>summary</c>, <c>hints</c>, <c>resolvedProcess</c>, <c>error</c>) is also compared so a
/// regression in surface text or hints is caught.</para>
/// <para>The live <c>CoreClrSample</c> fixture is reused so every view can attach to a real
/// process (<c>list</c> verifies the sample is visible; the others auto-resolve or target
/// it explicitly).</para>
/// </remarks>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class InspectProcessCompatibilityTests :
    IClassFixture<McpToolsTests.AuthedFactory>,
    IClassFixture<LiveCoreClrSampleFixture>
{
    private readonly McpToolsTests.AuthedFactory _factory;
    private readonly LiveCoreClrSampleFixture _sample;

    public InspectProcessCompatibilityTests(McpToolsTests.AuthedFactory factory, LiveCoreClrSampleFixture sample)
    {
        _factory = factory;
        _sample = sample;
    }

    [Fact]
    public async Task View_List_MatchesLegacy_ListDotnetProcesses()
    {
        await using var client = await ConnectAsync();

        // Two back-to-back list calls observe a different snapshot of the host (other
        // test workers / daemons start and stop). Mask data + summary (which embeds a
        // preview of the first three pids) so the assertion focuses on the structural
        // contract: same envelope shape, same hints, same error, same resolved-process
        // (which is null for list — it deliberately bypasses the resolver).
        var ignore = CompatibilityEnvelopeAssert.CompatibilityIgnore.Paths(
            "data",
            "summary",
            "hints/suggestedArguments/processId");

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var raw = await CallStructuredAsync(client, "list_dotnet_processes", new Dictionary<string, object?>());
                return RewrapLegacyEnvelope(raw);
            },
            successor: async () =>
            {
                var raw = await CallStructuredAsync(client, "inspect_process",
                    new Dictionary<string, object?> { ["view"] = "list" });
                return ExtractViewFromInspectProcess(raw, "list");
            },
            ignore: ignore);

        // Spot-check: a single inspect_process(view=list) call must populate data.list
        // with an array — proves the shape we masked away in the structural diff is
        // actually present and non-empty.
        var raw = await CallStructuredAsync(client, "inspect_process",
            new Dictionary<string, object?> { ["view"] = "list" });
        var data = raw.RootElement.GetProperty("data");
        data.GetProperty("view").GetString().Should().Be("list");
        data.GetProperty("list").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task View_Info_MatchesLegacy_GetProcessInfo()
    {
        await using var client = await ConnectAsync();
        var pid = _sample.ProcessId;

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var raw = await CallStructuredAsync(client, "get_process_info",
                    new Dictionary<string, object?> { ["processId"] = pid });
                return RewrapLegacyEnvelope(raw);
            },
            successor: async () =>
            {
                var raw = await CallStructuredAsync(client, "inspect_process",
                    new Dictionary<string, object?> { ["view"] = "info", ["processId"] = pid });
                return ExtractViewFromInspectProcess(raw, "info");
            });
    }

    [Fact]
    public async Task View_Capabilities_MatchesLegacy_GetDiagnosticCapabilities()
    {
        await using var client = await ConnectAsync();
        var pid = _sample.ProcessId;

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var raw = await CallStructuredAsync(client, "get_diagnostic_capabilities",
                    new Dictionary<string, object?> { ["processId"] = pid });
                return RewrapLegacyEnvelope(raw);
            },
            successor: async () =>
            {
                var raw = await CallStructuredAsync(client, "inspect_process",
                    new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = pid });
                return ExtractViewFromInspectProcess(raw, "capabilities");
            });
    }

    [Fact]
    public async Task View_Container_MatchesLegacy_GetContainerSignals()
    {
        await using var client = await ConnectAsync();
        var pid = _sample.ProcessId;

        // collectedAt + PSI averages drift between the two calls; mask them so we assert
        // on envelope shape (process id, in-container, cgroup version/path, structure of
        // sub-objects, hints and resolved-process digest).
        var ignore = CompatibilityEnvelopeAssert.CompatibilityIgnore.Paths(
            "data/collectedAt",
            "data/cpu",
            "data/memory",
            "data/pressure",
            "data/pids",
            "data/oomScore",
            "summary");

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var raw = await CallStructuredAsync(client, "get_container_signals",
                    new Dictionary<string, object?> { ["processId"] = pid });
                return RewrapLegacyEnvelope(raw);
            },
            successor: async () =>
            {
                var raw = await CallStructuredAsync(client, "inspect_process",
                    new Dictionary<string, object?> { ["view"] = "container", ["processId"] = pid });
                return ExtractViewFromInspectProcess(raw, "container");
            },
            ignore: ignore);
    }

    [Fact]
    public async Task View_MemoryTrend_MatchesLegacy_GetMemoryTrend()
    {
        await using var client = await ConnectAsync();
        var pid = _sample.ProcessId;

        // Memory trend samples wall-clock — two back-to-back calls observe different page-fault
        // counters and capture timestamps. Mask the volatile sample lists / collected-at /
        // deltas so the assertion focuses on the envelope shape contract.
        var ignore = CompatibilityEnvelopeAssert.CompatibilityIgnore.Paths(
            "data/samples",
            "data/deltas",
            "data/windowStart",
            "data/windowEnd",
            "data/verdict",
            "data/notes",
            "summary",
            "hints");

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var raw = await CallStructuredAsync(client, "get_memory_trend",
                    new Dictionary<string, object?>
                    {
                        ["processId"] = pid,
                        ["durationSeconds"] = 2,
                        ["sampleEverySeconds"] = 1,
                    });
                return RewrapLegacyEnvelope(raw);
            },
            successor: async () =>
            {
                var raw = await CallStructuredAsync(client, "inspect_process",
                    new Dictionary<string, object?>
                    {
                        ["view"] = "memory_trend",
                        ["processId"] = pid,
                        ["durationSeconds"] = 2,
                        ["sampleEverySeconds"] = 1,
                    });
                return ExtractViewFromInspectProcess(raw, "memoryTrend");
            },
            ignore: ignore);
    }

    [Fact]
    public async Task UnknownView_FailsWithStructuredInvalidArgument()
    {
        await using var client = await ConnectAsync();

        var raw = await CallStructuredAsync(client, "inspect_process",
            new Dictionary<string, object?> { ["view"] = "totally_bogus" });

        raw.RootElement.TryGetProperty("error", out var error).Should().BeTrue(
            "DiscriminatorDispatch.TryValidate must produce a structured failure envelope, not throw");
        error.GetProperty("kind").GetString().Should().Be("InvalidArgument");
        error.GetProperty("detail").GetString().Should().Be("view");
    }

    /// <summary>
    /// Calls a tool over MCP and returns the raw JSON envelope. The full envelope (summary,
    /// hints, data, error, resolvedProcess) is returned so the compatibility test can compare
    /// every field — not just the data payload.
    /// </summary>
    private static async Task<JsonDocument> CallStructuredAsync(
        McpClient client, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: CancellationToken.None);
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull($"tool {toolName} must return structured content or a text block");
            json = textBlock!.Text;
        }

        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Projects a legacy envelope into a normalized comparison shape: copies summary, hints,
    /// error, resolvedProcess (when present) and the data payload verbatim. Matches the shape
    /// <see cref="ExtractViewFromInspectProcess"/> produces so the two sides DeepEquals when
    /// the successor preserved the legacy contract.
    /// </summary>
    private static JsonDocument RewrapLegacyEnvelope(JsonDocument envelope)
    {
        var root = envelope.RootElement;
        var output = new Dictionary<string, JsonElement?>
        {
            ["summary"] = TryGet(root, "summary"),
            ["hints"] = TryGet(root, "hints"),
            ["error"] = TryGet(root, "error"),
            ["resolvedProcess"] = TryGet(root, "resolvedProcess"),
            ["data"] = TryGet(root, "data"),
        };
        return SerializeProjection(output);
    }

    /// <summary>
    /// Projects an inspect_process envelope into the same comparison shape, extracting the
    /// requested view-specific field out of <c>data</c> into <c>data</c> at the top level so
    /// it is structurally indistinguishable from the matching legacy envelope.
    /// </summary>
    private static JsonDocument ExtractViewFromInspectProcess(JsonDocument envelope, string fieldName)
    {
        var root = envelope.RootElement;
        var data = TryGet(root, "data");
        JsonElement? viewPayload = null;
        if (data is { ValueKind: JsonValueKind.Object } dataObj &&
            dataObj.TryGetProperty(fieldName, out var view))
        {
            viewPayload = view;
        }

        var output = new Dictionary<string, JsonElement?>
        {
            ["summary"] = TryGet(root, "summary"),
            ["hints"] = TryGet(root, "hints"),
            ["error"] = TryGet(root, "error"),
            ["resolvedProcess"] = TryGet(root, "resolvedProcess"),
            ["data"] = viewPayload,
        };
        return SerializeProjection(output);
    }

    private static JsonElement? TryGet(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) ? v : null;

    private static JsonDocument SerializeProjection(IReadOnlyDictionary<string, JsonElement?> projection)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in projection)
            {
                writer.WritePropertyName(key);
                if (value is null) writer.WriteNullValue();
                else value.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    private async Task<McpClient> ConnectAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", McpToolsTests.AuthedFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {McpToolsTests.AuthedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }
}
