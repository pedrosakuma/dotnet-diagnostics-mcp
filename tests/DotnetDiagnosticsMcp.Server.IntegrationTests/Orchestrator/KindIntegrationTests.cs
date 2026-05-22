using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Abstractions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

/// <summary>
/// End-to-end integration test for the central orchestrator (issue #20, last
/// acceptance bullet — P6). Spins up two replicas of the CoreClrSample target,
/// attaches to a specific one by label, runs <c>list_dotnet_processes</c>
/// through the orchestrator's reverse proxy, and confirms exactly one PID is
/// visible (the chosen Pod's) — proving the orchestrator forwarded the call
/// into the right Pod's PID namespace and not its sibling's.
/// </summary>
/// <remarks>
/// <para>
/// This test runs only when the environment variables documented below are
/// populated. The companion workflow
/// <c>.github/workflows/kind-integration.yml</c> sets them after spinning up a
/// kind cluster; everywhere else the test method returns early so it does not
/// fail the regular ubuntu/windows CI legs. The cleaner skip semantics provided
/// by <c>Xunit.SkippableFact</c> would require adding a new package — see
/// <c>AGENTS.md</c> on package hygiene and the per-task instruction to keep the
/// dependency footprint stable. We use the test filter
/// <c>--filter Category!=KindIntegration</c> in <c>ci.yml</c> as a belt-and-braces
/// guard so the early-return is never silently masking a real failure.
/// </para>
/// <para>
/// Required environment variables (all five must be set; otherwise the test is
/// a no-op):
/// <list type="bullet">
/// <item><c>DOTNET_DBG_MCP_KIND_TEST=1</c> — opt-in gate.</item>
/// <item><c>DOTNET_DBG_MCP_ORCH_URL</c> — base URL of the orchestrator MCP endpoint, e.g. <c>http://127.0.0.1:5130</c>.</item>
/// <item><c>DOTNET_DBG_MCP_ORCH_TOKEN</c> — bearer token the orchestrator was deployed with.</item>
/// <item><c>DOTNET_DBG_MCP_KIND_NAMESPACE</c> — namespace the sample pods live in (default: <c>p6-sample</c>).</item>
/// <item><c>DOTNET_DBG_MCP_KIND_TARGET_LABEL</c> — discriminator label key=value, e.g. <c>p6-target=a</c>.</item>
/// </list>
/// </para>
/// </remarks>
[Trait("Category", "KindIntegration")]
public sealed class KindIntegrationTests
{
    private const string EnableEnvVar = "DOTNET_DBG_MCP_KIND_TEST";
    private const string OrchestratorUrlEnvVar = "DOTNET_DBG_MCP_ORCH_URL";
    private const string OrchestratorTokenEnvVar = "DOTNET_DBG_MCP_ORCH_TOKEN";
    private const string KindNamespaceEnvVar = "DOTNET_DBG_MCP_KIND_NAMESPACE";
    private const string KindTargetLabelEnvVar = "DOTNET_DBG_MCP_KIND_TARGET_LABEL";
    private const string DefaultNamespace = "p6-sample";

    private readonly ITestOutputHelper _output;

    public KindIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AttachToLabeledReplica_ListsExactlyOnePidThroughProxy()
    {
        if (!TryGetActivation(out var activation))
        {
            _output.WriteLine("KindIntegrationTests: opt-in env vars missing; skipping.");
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // ----------------------------------------------------------------
        // Step 1: Open an MCP client against the orchestrator (root MCP).
        // ----------------------------------------------------------------
        await using var orchClient = await ConnectAsync(
            new Uri(activation.OrchestratorBaseUrl, "/mcp"),
            activation.BearerToken,
            ct).ConfigureAwait(false);

        _output.WriteLine($"Connected to orchestrator at {activation.OrchestratorBaseUrl}");

        // ----------------------------------------------------------------
        // Step 2: list_pods with the discriminating label selector.
        // ----------------------------------------------------------------
        var labelSelector = $"app=p6-sample,{activation.TargetLabel}";
        var listResult = await orchClient.CallToolAsync(
            "list_pods",
            new Dictionary<string, object?>
            {
                ["namespace"] = activation.Namespace,
                ["labelSelector"] = labelSelector,
                ["preparedOnly"] = true,
                ["limit"] = 10,
            },
            cancellationToken: ct).ConfigureAwait(false);

        listResult.IsError.Should().NotBe(true, "list_pods must succeed for a prepared sample namespace");
        var listEnvelope = DeserializeEnvelope(listResult);
        listEnvelope.Should().NotBeNull();
        listEnvelope!.Error.Should().BeNull("list_pods returned a structured DiagnosticError: " + listEnvelope.Summary);

        var items = listEnvelope.Data.GetProperty("items");
        items.GetArrayLength().Should().Be(
            1,
            $"label selector '{labelSelector}' must match exactly one prepared pod (got {items.GetArrayLength()})");

        var chosen = items[0];
        var chosenPodName = chosen.GetProperty("name").GetString()!;
        var chosenContainer = chosen.GetProperty("containerName").GetString();
        _output.WriteLine($"Chosen pod: {activation.Namespace}/{chosenPodName} container={chosenContainer}");

        // ----------------------------------------------------------------
        // Step 3: attach_to_pod against the chosen replica.
        // ----------------------------------------------------------------
        var attachResult = await orchClient.CallToolAsync(
            "attach_to_pod",
            new Dictionary<string, object?>
            {
                ["namespace"] = activation.Namespace,
                ["podName"] = chosenPodName,
                ["containerName"] = chosenContainer,
                ["requirePreparedTarget"] = true,
                ["allowReuseExistingSession"] = true,
                ["ttlSeconds"] = 600,
            },
            cancellationToken: ct).ConfigureAwait(false);

        attachResult.IsError.Should().NotBe(true, "attach_to_pod must succeed against a prepared replica");
        var attachEnvelope = DeserializeStructured<AttachSession>(attachResult);
        attachEnvelope.Should().NotBeNull();
        attachEnvelope!.State.Should().Be(InvestigationState.Active,
            $"attach must reach Active (got {attachEnvelope.State}; reason='{attachEnvelope.FailureReason}')");
        attachEnvelope.PodName.Should().Be(chosenPodName);
        attachEnvelope.ProxyBaseUrl.Should().NotBeNullOrWhiteSpace(
            "an Active handle must publish the orchestrator-relative proxy prefix");

        var proxyBase = attachEnvelope.ProxyBaseUrl!.TrimStart('/');
        _output.WriteLine($"Attached: handleId={attachEnvelope.HandleId} proxy={attachEnvelope.ProxyBaseUrl}");

        try
        {
            // ------------------------------------------------------------
            // Step 4: open a second MCP client against the proxied URL and
            // call list_dotnet_processes — it MUST see exactly one process
            // (the chosen Pod's CoreClrSample), proving the orchestrator
            // forwarded into the right Pod's PID namespace.
            // ------------------------------------------------------------
            var proxyEndpoint = new Uri(activation.OrchestratorBaseUrl, $"/{proxyBase}/mcp");
            await using var podClient = await ConnectAsync(proxyEndpoint, activation.BearerToken, ct);

            var listProcsResult = await podClient.CallToolAsync(
                "list_dotnet_processes",
                arguments: null,
                cancellationToken: ct).ConfigureAwait(false);

            listProcsResult.IsError.Should().NotBe(true,
                "list_dotnet_processes must succeed against the proxied pod");
            var procs = DeserializeStructured<IReadOnlyList<DotnetProcess>>(listProcsResult);
            procs.Should().NotBeNull();
            foreach (var p in procs!)
            {
                _output.WriteLine(
                    $"proxied list_dotnet_processes -> pid={p.ProcessId} entry={p.ManagedEntrypointAssemblyName} cmd={p.CommandLine}");
            }

            procs.Should().HaveCount(1,
                "the proxied list_dotnet_processes must surface only the chosen Pod's CoreClrSample process — " +
                "the sibling replica lives in a separate PID namespace and must not appear");
            procs[0].ManagedEntrypointAssemblyName.Should().Be("CoreClrSample",
                "the chosen pod's process must be the CoreClrSample entrypoint");

            // ------------------------------------------------------------
            // Step 5: detach_from_pod and confirm the handle is Closed.
            // ------------------------------------------------------------
            var detachResult = await orchClient.CallToolAsync(
                "detach_from_pod",
                new Dictionary<string, object?> { ["handleId"] = attachEnvelope.HandleId },
                cancellationToken: ct).ConfigureAwait(false);

            detachResult.IsError.Should().NotBe(true, "detach_from_pod is idempotent and must succeed");
            var detached = DeserializeStructured<DetachResult>(detachResult);
            detached.Should().NotBeNull();
            detached!.Found.Should().BeTrue();
            detached.NewState.Should().Be(InvestigationState.Closed,
                "detach_from_pod must transition Active -> Closed");
        }
        catch
        {
            // Best-effort cleanup so a partial failure doesn't leak a live attach
            // behind the kind cluster's TTL reaper.
            try
            {
                await orchClient.CallToolAsync(
                    "detach_from_pod",
                    new Dictionary<string, object?> { ["handleId"] = attachEnvelope.HandleId },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallow — the orchestrator may already have torn the handle down.
            }
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // Activation + transport helpers.
    // -----------------------------------------------------------------------

    private static bool TryGetActivation(out Activation activation)
    {
        activation = default!;
        var gate = Environment.GetEnvironmentVariable(EnableEnvVar);
        if (!string.Equals(gate, "1", StringComparison.Ordinal) &&
            !string.Equals(gate, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var url = Environment.GetEnvironmentVariable(OrchestratorUrlEnvVar);
        var token = Environment.GetEnvironmentVariable(OrchestratorTokenEnvVar);
        var label = Environment.GetEnvironmentVariable(KindTargetLabelEnvVar);
        var ns = Environment.GetEnvironmentVariable(KindNamespaceEnvVar);

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(label))
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        activation = new Activation(
            OrchestratorBaseUrl: baseUri,
            BearerToken: token!,
            Namespace: string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns!,
            TargetLabel: label!);
        return true;
    }

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string bearer, CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient { BaseAddress = endpoint };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {bearer}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, clientOptions: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static T? DeserializeStructured<T>(ModelContextProtocol.Protocol.CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            text.Should().NotBeNull("tool must return either structured content or a text block");
            json = text!.Text;
        }
        var envelope = JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().BeNull("structured response surfaced an error envelope: " + envelope.Summary);
        return envelope.Data;
    }

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(ModelContextProtocol.Protocol.CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            text.Should().NotBeNull();
            json = text!.Text;
        }
        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
    }

    private sealed record Activation(
        Uri OrchestratorBaseUrl,
        string BearerToken,
        string Namespace,
        string TargetLabel);
}
