using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class McpToolsTests : IClassFixture<McpToolsTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public McpToolsTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListTools_ExposesEveryCoreToolWithSchema()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "list_dotnet_processes",
            "get_process_info",
            "get_diagnostic_capabilities",
            "snapshot_counters",
            "collect_cpu_sample",
            "collect_exceptions",
            "collect_gc_events",
            "collect_event_source",
            "collect_process_dump",
            "inspect_dump",
            "inspect_live_heap",
            "query_heap_snapshot",
            "collect_thread_snapshot",
            "query_thread_snapshot",
            "get_call_tree",
            "start_investigation",
            "export_investigation_summary",
            "compare_to_baseline",
            "get_collection_status",
            "cancel_collection");

        // Tools that historically required `processId` are now bootstrap-implicit (issue #42):
        // when omitted the server auto-selects the lone .NET process visible to it. The only
        // genuinely required parameters left are domain values the LLM cannot guess (provider
        // names, handles, dump paths, snapshot blobs). `collect_event_source` keeps
        // `providerName` as required because there is no sensible default.
        var allowedRequired = new Dictionary<string, string[]>
        {
            ["list_dotnet_processes"] = Array.Empty<string>(),
            ["get_process_info"] = Array.Empty<string>(),
            ["get_diagnostic_capabilities"] = Array.Empty<string>(),
            ["snapshot_counters"] = Array.Empty<string>(),
            ["collect_cpu_sample"] = Array.Empty<string>(),
            ["collect_exceptions"] = Array.Empty<string>(),
            ["collect_gc_events"] = Array.Empty<string>(),
            ["collect_event_source"] = new[] { "providerName" },
            ["collect_process_dump"] = Array.Empty<string>(),
            ["inspect_dump"] = new[] { "dumpFilePath" },
            ["inspect_live_heap"] = Array.Empty<string>(),
            ["query_heap_snapshot"] = new[] { "handle" },
            ["collect_thread_snapshot"] = Array.Empty<string>(),
            ["query_thread_snapshot"] = new[] { "handle" },
            ["get_call_tree"] = new[] { "handle" },
            ["start_investigation"] = Array.Empty<string>(),
            ["export_investigation_summary"] = new[] { "handle" },
            ["compare_to_baseline"] = new[] { "baselineSummaryJson", "currentSummaryJson" },
            ["get_collection_status"] = new[] { "handle" },
            ["cancel_collection"] = new[] { "handle" },
        };

        // The spirit of elicit-graceful: no user-facing parameter (durationSeconds, topN,
        // maxRecent, maxEvents, eventLevel, dumpType, outputDirectory, rootMethodFilter,
        // maxDepth, maxNodes) should ever be required. The minimal required set must include
        // the small allowed list per tool. We don't assert exact equality because SDK 1.3.0
        // sporadically lists DI-injected service parameters in the JSON schema when the
        // service-provider scope differs from the one used at schema generation — this is
        // harmless on the wire (those params can never come from the LLM) but breaks strict
        // equality assertions.
        var mustNotBeRequired = new[]
        {
            "processId",
            "durationSeconds", "topN", "maxRecent", "maxEvents", "eventLevel",
            "dumpType", "outputDirectory", "rootMethodFilter", "maxDepth", "maxNodes",
            "intervalSeconds", "symptom", "hypothesis", "baseline", "maxToolCalls",
            "dumpRequiresApproval", "format", "topHotspots", "buildAssemblyName",
            "previousInvestigationId", "fixCommitSha", "fixPullRequestUrl", "fixDescription", "notes",
            "resolveSourceLines", "symbolPath", "maxResolvedSources",
            "topTypes", "includeRetentionPaths", "retentionPathLimit",
            "runAsJob",
        };

        foreach (var tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace($"tool {tool.Name} must document itself for the LLM");
            tool.JsonSchema.ValueKind.Should().Be(JsonValueKind.Object);
            tool.Title.Should().NotBeNullOrWhiteSpace(
                $"tool {tool.Name} must declare a Title — surfaced in Claude Code / Copilot CLI pickers");
            tool.Name.Should().MatchRegex("^[A-Za-z0-9_\\-.]{1,128}$");
            tool.ReturnJsonSchema.Should().NotBeNull(
                $"tool {tool.Name} must declare an outputSchema (UseStructuredContent = true)");
            tool.ReturnJsonSchema!.Value.ValueKind.Should().Be(JsonValueKind.Object);

            var required = tool.JsonSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                ? req.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();

            foreach (var minimum in allowedRequired[tool.Name])
            {
                required.Should().Contain(minimum,
                    $"tool {tool.Name} must require {minimum}");
            }

            foreach (var forbidden in mustNotBeRequired)
            {
                required.Should().NotContain(forbidden,
                    $"tool {tool.Name}: parameter '{forbidden}' must keep its default so the LLM can call the tool without elicitation");
            }
        }
    }

    [Fact]
    public async Task ListPrompts_ExposesDiagnosticPlaybooks()
    {
        await using var client = await ConnectAsync();

        var prompts = await client.ListPromptsAsync(cancellationToken: CancellationToken.None);

        prompts.Select(p => p.Name).Should().BeEquivalentTo(
            "diagnose-high-latency",
            "diagnose-memory-growth",
            "diagnose-5xx-errors",
            "diagnose-slow-outbound-http",
            "triage-nativeaot",
            "diagnose-safely-in-prod");

        foreach (var prompt in prompts)
        {
            prompt.Description.Should().NotBeNullOrWhiteSpace($"prompt {prompt.Name} must document itself");
            prompt.Title.Should().NotBeNullOrWhiteSpace($"prompt {prompt.Name} must declare a Title for pickers");
        }
    }

    [Fact]
    public async Task GetPrompt_RendersDiagnoseHighLatencyWithProcessId()
    {
        await using var client = await ConnectAsync();

        var result = await client.GetPromptAsync(
            "diagnose-high-latency",
            new Dictionary<string, object?> { ["processId"] = 4242 },
            cancellationToken: CancellationToken.None);

        result.Messages.Should().NotBeEmpty();
        var msg = result.Messages.Single();
        msg.Role.Should().Be(ModelContextProtocol.Protocol.Role.User);

        var block = msg.Content.Should().BeOfType<ModelContextProtocol.Protocol.TextContentBlock>().Subject;
        block.Text.Should().Contain("4242", "prompt body must interpolate the supplied processId");
        block.Text.Should().Contain("snapshot_counters", "prompt must steer the LLM through the standard tool chain");
        block.Annotations.Should().NotBeNull("audience metadata must be present per issue #44");
        block.Annotations!.Audience.Should().NotBeNull();
        block.Annotations.Audience!.Should().Contain(ModelContextProtocol.Protocol.Role.Assistant,
            "prompts target the LLM, not the human user");
    }

    [Fact]
    public async Task GetPrompt_RendersDiagnoseHighLatencyWithoutProcessId()
    {
        await using var client = await ConnectAsync();

        var result = await client.GetPromptAsync(
            "diagnose-high-latency",
            arguments: null,
            cancellationToken: CancellationToken.None);

        var text = string.Join("\n", result.Messages
            .Select(m => m.Content)
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Select(b => b.Text));

        text.Should().Contain("snapshot_counters(durationSeconds=",
            "when processId is omitted the body must drop the processId argument so bootstrap implícito kicks in");
        text.Should().NotContain("processId=",
            "no processId placeholder must leak into the rendered playbook when none was supplied");
    }

    [Fact]
    public async Task ListResources_ExposesInvestigationGuide()
    {
        await using var client = await ConnectAsync();

        var resources = await client.ListResourcesAsync(cancellationToken: CancellationToken.None);

        resources.Should().Contain(r => r.Uri == "diag://guides/investigation",
            "the investigation playbook must be reachable as a Resource");
    }

    [Fact]
    public async Task ListResourceTemplates_ExposesTraceSession()
    {
        await using var client = await ConnectAsync();

        var templates = await client.ListResourceTemplatesAsync(cancellationToken: CancellationToken.None);

        templates.Should().Contain(t => t.UriTemplate == "trace://session/{handle}",
            "trace://session/{handle} must be advertised so clients can pull drill-down artifacts directly");
    }

    [Fact]
    public async Task ReadResource_ReturnsUnknownPayloadForExpiredHandle()
    {
        await using var client = await ConnectAsync();

        var result = await client.ReadResourceAsync(
            "trace://session/DEADBEEFDEADBEEFDEAD",
            cancellationToken: CancellationToken.None);

        result.Contents.Should().NotBeEmpty();
        var text = result.Contents
            .OfType<ModelContextProtocol.Protocol.TextResourceContents>()
            .Select(c => c.Text)
            .FirstOrDefault();
        text.Should().NotBeNullOrWhiteSpace();
        text!.Should().Contain("unknown",
            "expired/unknown handles must serialize a deterministic JSON body so consumers can branch");
    }

    [Fact]
    public async Task Initialize_AdvertisesServerInfoAndInstructions()
    {
        // Pin the spec version we advertise so a future SDK bump that changes the default
        // doesn't silently degrade the negotiated version.
        var clientOptions = new ModelContextProtocol.Client.McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
        };

        await using var client = await ConnectAsync(clientOptions);

        client.ServerInfo.Should().NotBeNull();
        client.ServerInfo!.Name.Should().Be("dotnet-diagnostics-mcp");
        client.ServerInfo.Title.Should().Be(".NET Diagnostics");
        client.ServerInfo.Description.Should().NotBeNullOrWhiteSpace(
            "serverInfo.description is required for low-context LLMs to identify what this server is for");
        client.ServerInfo.WebsiteUrl.Should().Be("https://github.com/pedrosakuma/dotnet-diagnostics-mcp");

        client.ServerInstructions.Should().NotBeNullOrWhiteSpace(
            "instructions are surfaced verbatim by clients on session start");
        client.ServerInstructions.Should().Contain("list_dotnet_processes",
            "instructions must steer the model to the documented call order");
    }

    [Fact]
    public async Task ListDotnetProcesses_FindsSelfHostedTestProcess()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "list_dotnet_processes",
            arguments: null,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var processes = DeserializeStructured<List<DotnetProcess>>(result);
        processes.Should().NotBeNull();
        processes!.Should().Contain(p => p.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task GetProcessInfo_ReturnsSelf()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_process_info",
            new Dictionary<string, object?> { ["processId"] = Environment.ProcessId },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var process = DeserializeStructured<DotnetProcess>(result);
        process.Should().NotBeNull();
        process!.ProcessId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task GetDiagnosticCapabilities_ReportsCoreClrForTestHost()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_diagnostic_capabilities",
            new Dictionary<string, object?> { ["processId"] = Environment.ProcessId },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var caps = DeserializeStructured<DiagnosticCapabilities>(result);
        caps.Should().NotBeNull();
        caps!.Runtime.Should().Be(RuntimeFlavor.CoreClr);
        caps.CanSampleCpu.Should().BeTrue();
        caps.CanReadEventCounters.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotCounters_ReturnsRuntimeCounters()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "snapshot_counters",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["providers"] = new[] { "System.Runtime" },
                ["intervalSeconds"] = 1,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var snapshot = DeserializeStructured<CounterSnapshot>(result);
        snapshot.Should().NotBeNull();
        snapshot!.Counters.Should().NotBeEmpty();
        snapshot.Counters.Should().Contain(c => c.Provider == "System.Runtime");
    }

    [Fact]
    public async Task CollectExceptions_RunsAgainstSelfHost()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "collect_exceptions",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
                ["maxRecent"] = 10,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var snapshot = DeserializeStructured<ExceptionSnapshot>(result);
        snapshot.Should().NotBeNull();
        snapshot!.ProcessId.Should().Be(Environment.ProcessId);
        snapshot.TotalExceptions.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CollectGcEvents_RunsAgainstSelfHost()
    {
        await using var client = await ConnectAsync();

        // Encourage at least one GC so the test exercises the parsing path.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        var result = await client.CallToolAsync(
            "collect_gc_events",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxEvents"] = 50,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var summary = DeserializeStructured<GcSummary>(result);
        summary.Should().NotBeNull();
        summary!.ProcessId.Should().Be(Environment.ProcessId);
        // Force a few more during the window so events actually land.
        _ = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                Thread.Sleep(200);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }
        });
    }

    [Fact]
    public async Task CollectEventSource_CapturesSystemRuntimeEvents()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "collect_event_source",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["providerName"] = "System.Runtime",
                ["durationSeconds"] = 2,
                ["maxEvents"] = 50,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var capture = DeserializeStructured<EventSourceCapture>(result);
        capture.Should().NotBeNull();
        capture!.Provider.Should().Be("System.Runtime");
    }

    [Fact]
    public async Task CollectProcessDump_WritesMiniDumpToDisk()
    {
        await using var client = await ConnectAsync();

        var directory = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-tests-{Guid.NewGuid():N}");
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = directory,
                },
                cancellationToken: CancellationToken.None);

            result.IsError.Should().NotBe(true);
            var dump = DeserializeStructured<DumpResult>(result);
            dump.Should().NotBeNull();
            dump!.ProcessId.Should().Be(Environment.ProcessId);
            dump.FilePath.Should().StartWith(directory);
            File.Exists(dump.FilePath).Should().BeTrue();
            dump.FileSizeBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // best effort cleanup
            }
        }
    }

    [Fact]
    public async Task GetCallTree_ReturnsHandleExpiredErrorForUnknownHandle()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_call_tree",
            new Dictionary<string, object?>
            {
                ["handle"] = "DEADBEEFDEADBEEFDEAD",
                ["maxDepth"] = 4,
                ["maxNodes"] = 50,
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("an unknown handle must surface a structured DiagnosticError");
        envelope.Error!.Kind.Should().Be("HandleExpired");
        envelope.Hints.Should().NotBeEmpty();
        envelope.Hints[0].NextTool.Should().Be("collect_cpu_sample");
    }

    [Fact]
    public async Task StartInvestigation_ReturnsColdPlan_WhenOnlySymptomProvided()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "start_investigation",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["symptom"] = "high latency on /checkout endpoint",
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var plan = DeserializeStructured<DotnetDiagnosticsMcp.Core.Investigation.InvestigationPlan>(result);
        plan.Should().NotBeNull();
        plan!.Mode.Should().Be(DotnetDiagnosticsMcp.Core.Investigation.InvestigationMode.Cold);
        plan.NextStep.ToolName.Should().Be("snapshot_counters");
        plan.Constraints.MaxToolCalls.Should().Be(8);
        plan.AllSteps.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task StartInvestigation_RoutesHypothesisDirectlyToContentionEvents()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "start_investigation",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["hypothesis"] = "lock contention on Cart.Checkout after release v2025.10",
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var plan = DeserializeStructured<DotnetDiagnosticsMcp.Core.Investigation.InvestigationPlan>(result);
        plan.Should().NotBeNull();
        plan!.Mode.Should().Be(DotnetDiagnosticsMcp.Core.Investigation.InvestigationMode.Hypothesis);
        plan.NextStep.ToolName.Should().Be("collect_event_source");
        plan.EarlyStopConditions.Select(e => e.ConditionId).Should().Contain("hypothesis-confirmed");
    }

    [Fact]
    public async Task ExportInvestigationSummary_ReturnsHandleExpiredErrorForUnknownHandle()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "export_investigation_summary",
            new Dictionary<string, object?> { ["handle"] = "DEADBEEFDEADBEEFDEAD" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error!.Kind.Should().Be("HandleExpired");
    }

    [Fact]
    public async Task CompareToBaseline_RejectsMalformedJson()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "compare_to_baseline",
            new Dictionary<string, object?>
            {
                ["baselineSummaryJson"] = "{not json",
                ["currentSummaryJson"] = "{not json either",
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error!.Kind.Should().Be("InvalidSummaryJson");
    }

    [Fact]
    public async Task CompareToBaseline_RejectsSchemaValidButIncompleteJson()
    {
        await using var client = await ConnectAsync();

        const string incomplete = "{\"Schema\":\"dotnet-diagnostics-mcp/investigation-summary/v1\"}";
        var result = await client.CallToolAsync(
            "compare_to_baseline",
            new Dictionary<string, object?>
            {
                ["baselineSummaryJson"] = incomplete,
                ["currentSummaryJson"] = incomplete,
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error!.Kind.Should().Be("InvalidSummaryJson");
    }

    [Fact]
    public async Task CallTool_RejectsInvalidArguments()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "snapshot_counters",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 0,
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("invalid arguments must surface a structured DiagnosticError");
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        envelope.Hints.Should().NotBeEmpty("error responses must include at least one recovery hint");
    }

    private async Task<McpClient> ConnectAsync(ModelContextProtocol.Client.McpClientOptions? clientOptions = null)
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

        return await McpClient.CreateAsync(transport, clientOptions, cancellationToken: CancellationToken.None);
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
            var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull("tool must return either structured content or a text block");
            json = textBlock!.Text;
        }

        var envelope = JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull("structured payload must deserialize as DiagnosticResult<T>");
        envelope!.Summary.Should().NotBeNullOrWhiteSpace("every response must include a summary");
        envelope.Hints.Should().NotBeNull("hints array is mandatory (may be empty)");
        envelope.Error.Should().BeNull("successful responses must not carry an error");
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
            var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull();
            json = textBlock!.Text;
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
