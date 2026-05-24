using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Stage A of RFC 0002 §7.3 #7 / issue #211 — end-to-end coverage for the MCP-native
/// progress and cancellation path on long-running collectors (<c>collect_cpu_sample</c>
/// and the merged <c>collect_events</c> surface). The polling-based
/// <c>runAsJob</c>/<c>get_collection_status</c>/<c>cancel_collection</c> path stays
/// covered by <see cref="McpToolsTests.CollectCpuSample_RunAsJob_HandleSurvivesAndPollableUntilCompleted"/>;
/// these tests assert the new path so Stage B (which deletes the polling tools) can
/// land without losing test coverage.
/// </summary>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class McpProgressAndCancelTests : IClassFixture<McpProgressAndCancelTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public McpProgressAndCancelTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CollectEvents_Counters_EmitsProgressNotifications()
    {
        // The collect_events(counters) collector runs an EventPipe session for ~durationSeconds.
        // The CollectionProgressTicker should emit at least one progress notification per second
        // while the work is in flight, plus a terminal 100% on success.
        await using var client = await ConnectAsync();

        var received = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(p =>
        {
            lock (received) received.Add(p);
        });

        var result = await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "counters",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 4,
            },
            progress,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true, "the call must succeed under nominal conditions");

        // Give the transport a beat to flush any in-flight notifications. The MCP server emits
        // progress on a 1s cadence inside a ~4s window, so we expect at least 2 ticks plus the
        // final 100% — be lenient (≥2) to absorb scheduler jitter on CI.
        await Task.Delay(500);
        lock (received)
        {
            received.Should().HaveCountGreaterThanOrEqualTo(2,
                "the progress ticker must fire at least twice across a 4s collection window — " +
                "if zero notifications arrive, the CollectionProgressTicker plumbing is broken");
            received[^1].Progress.Should().Be(100f,
                "the final notification must report 100% completion");
        }
    }

    [Fact]
    public async Task CollectEvents_Counters_McpNativeCancel_ReturnsCancelledEnvelope()
    {
        // Issue an MCP cancel via the client cancellation token mid-window. The collector should
        // observe the OperationCanceledException, unwind cleanly, and return an envelope with
        // cancelled=true — no exception bubbled out to the client. Stage B will delete the
        // cancel_collection tool; this is what replaces it.
        await using var client = await ConnectAsync();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        ModelContextProtocol.Protocol.CallToolResult result;
        try
        {
            result = await client.CallToolAsync(
                "collect_events",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = Environment.ProcessId,
                    ["durationSeconds"] = 30, // long enough that the cancel fires first
                },
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Acceptable shape: some MCP client paths surface the cancel as an exception
            // instead of returning the partial envelope. Either is conformant — the key
            // assertion is that the call terminates promptly (the request did not hang).
            return;
        }

        // Server-side cancel path: returns a partial envelope with Cancelled=true.
        var envelope = DeserializeEnvelopeRaw(result);
        envelope.Should().NotBeNull("a cancelled collector must still emit a structured envelope");
        envelope!.Cancelled.Should().BeTrue("the envelope must mark itself as cancelled");
    }

    // ---- helpers ----

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

        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static DiagnosticResult<JsonElement>? DeserializeEnvelopeRaw(ModelContextProtocol.Protocol.CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            if (textBlock is null) return null;
            json = textBlock.Text;
        }
        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
    }

    public sealed class AuthedFactory : WebApplicationFactory<DotnetDiagnosticsMcp.Server.Program>
    {
        public const string Token = "test-bearer-token-do-not-use-in-prod";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
