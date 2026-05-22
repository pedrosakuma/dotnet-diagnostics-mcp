using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class McpToolsTests : IClassFixture<McpToolsTests.AuthedFactory>
{
    private static readonly ActivitySource IntegrationActivitySource = new("DotnetDiagnosticsMcp.Server.IntegrationTests.Activities");

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
            "collect_allocation_sample",
            "collect_exceptions",
            "collect_gc_events",
            "collect_activities",
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
            "cancel_collection",
            "query_collection",
            "get_container_signals",
            "get_memory_trend",
            "collect_off_cpu_sample",
            "query_off_cpu_snapshot",
            "capture_method_bytes");

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
            ["collect_allocation_sample"] = Array.Empty<string>(),
            ["collect_exceptions"] = Array.Empty<string>(),
            ["collect_gc_events"] = Array.Empty<string>(),
            ["collect_activities"] = Array.Empty<string>(),
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
            ["query_collection"] = new[] { "handle" },
            ["get_container_signals"] = Array.Empty<string>(),
            ["get_memory_trend"] = Array.Empty<string>(),
            ["collect_off_cpu_sample"] = Array.Empty<string>(),
            ["query_off_cpu_snapshot"] = new[] { "handle" },
            ["capture_method_bytes"] = new[] { "moduleVersionId", "metadataToken" },
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
            "durationSeconds", "topN", "maxRecent", "maxEvents", "maxActivities", "eventLevel",
            "dumpType", "outputDirectory", "rootMethodFilter", "maxDepth", "maxNodes",
            "intervalSeconds", "sampleEverySeconds", "sources", "symptom", "hypothesis", "baseline", "maxToolCalls",
            "dumpRequiresApproval", "format", "topHotspots", "buildAssemblyName",
            "previousInvestigationId", "fixCommitSha", "fixPullRequestUrl", "fixDescription", "notes",
            "resolveSourceLines", "symbolPath", "maxResolvedSources",
            "resolveMethodInstantiations", "maxResolvedMethodInstantiations",
            "topTypes", "includeRetentionPaths", "retentionPathLimit",
            "runAsJob",
            "view",
            "stackRank",
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

            if (tool.Name is "collect_off_cpu_sample" or "inspect_dump" or "inspect_live_heap" or "collect_thread_snapshot")
            {
                var properties = tool.JsonSchema.GetProperty("properties");
                properties.TryGetProperty("symbolPath", out _).Should().BeTrue($"tool {tool.Name} must expose the symbolPath override");
            }
        }
    }

    [Fact]
    public async Task TasksCapability_AndToolMetadata_AreAdvertised()
    {
        await using var client = await ConnectAsync();

        client.ServerCapabilities.Tasks.Should().NotBeNull();
        client.ServerCapabilities.Tasks!.List.Should().NotBeNull();
        client.ServerCapabilities.Tasks.Cancel.Should().NotBeNull();
        client.ServerCapabilities.Tasks.Requests.Should().NotBeNull();
        client.ServerCapabilities.Tasks.Requests!.Tools.Should().NotBeNull();
        client.ServerCapabilities.Tasks.Requests.Tools!.Call.Should().NotBeNull();

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        foreach (var toolName in new[] { "collect_cpu_sample", "collect_exceptions", "collect_gc_events" })
        {
            var tool = tools.Single(t => t.Name == toolName);
            tool.ProtocolTool.Execution.Should().NotBeNull($"{toolName} must advertise execution metadata for MCP Tasks");
            tool.ProtocolTool.Execution!.TaskSupport.Should().Be(ModelContextProtocol.Protocol.ToolTaskSupport.Optional);
        }
    }

    [Fact]
    public async Task TaskAugmentedCollectCpuSample_RoundTripsThroughSpecTasks()
    {
        await using var client = await ConnectAsync();

        var task = await client.CallToolAsTaskAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 1,
                ["topN"] = 5,
                ["resolveSourceLines"] = false,
            },
            new ModelContextProtocol.Protocol.McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) },
            cancellationToken: CancellationToken.None);

        task.TaskId.Should().NotBeNullOrWhiteSpace();
        task.Status.Should().Be(ModelContextProtocol.Protocol.McpTaskStatus.Working);
        task.PollInterval.Should().NotBeNull();

        var listed = await client.ListTasksAsync(cancellationToken: CancellationToken.None);
        listed.Select(t => t.TaskId).Should().Contain(task.TaskId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        ModelContextProtocol.Protocol.McpTask terminal = task;
        while (DateTime.UtcNow < deadline)
        {
            terminal = await client.GetTaskAsync(task.TaskId, cancellationToken: CancellationToken.None);
            if (terminal.Status is ModelContextProtocol.Protocol.McpTaskStatus.Completed or ModelContextProtocol.Protocol.McpTaskStatus.Failed or ModelContextProtocol.Protocol.McpTaskStatus.Cancelled)
            {
                break;
            }

            await Task.Delay(terminal.PollInterval ?? TimeSpan.FromMilliseconds(200));
        }

        terminal.Status.Should().Be(ModelContextProtocol.Protocol.McpTaskStatus.Completed);

        var rawResult = await client.GetTaskResultAsync(task.TaskId, cancellationToken: CancellationToken.None);
        var callToolResult = JsonSerializer.Deserialize<ModelContextProtocol.Protocol.CallToolResult>(rawResult.GetRawText(), DeserializeOptions);
        callToolResult.Should().NotBeNull();
        callToolResult!.IsError.Should().NotBe(true);

        var envelope = DeserializeEnvelope(callToolResult);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().BeNull();
        envelope.Data.GetProperty("processId").GetInt32().Should().Be(Environment.ProcessId);
        envelope.Data.GetProperty("totalSamples").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TaskAugmentedCollectExceptions_CanBeCancelledViaLegacyBridge()
    {
        await using var client = await ConnectAsync();

        var task = await client.CallToolAsTaskAsync(
            "collect_exceptions",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 10,
                ["maxRecent"] = 10,
            },
            new ModelContextProtocol.Protocol.McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) },
            cancellationToken: CancellationToken.None);

        var cancelResult = await client.CallToolAsync(
            "cancel_collection",
            new Dictionary<string, object?> { ["handle"] = task.TaskId },
            cancellationToken: CancellationToken.None);

        cancelResult.IsError.Should().NotBe(true);
        var cancelReport = DeserializeStructured<CancelCollectionReport>(cancelResult);
        cancelReport.Should().NotBeNull();
        cancelReport!.CancellationRequested.Should().BeTrue();

        var bridgedStatus = await PollCollectionStatusAsync(client, task.TaskId);
        bridgedStatus.Error.Should().BeNull();
        bridgedStatus.Status.Should().BeOneOf("working", "cancelled");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ModelContextProtocol.Protocol.McpTask terminal = task;
        while (DateTime.UtcNow < deadline)
        {
            terminal = await client.GetTaskAsync(task.TaskId, cancellationToken: CancellationToken.None);
            if (terminal.Status == ModelContextProtocol.Protocol.McpTaskStatus.Cancelled)
            {
                break;
            }

            await Task.Delay(200);
        }

        terminal.Status.Should().Be(ModelContextProtocol.Protocol.McpTaskStatus.Cancelled);

        var finalBridgeStatus = await PollCollectionStatusAsync(client, task.TaskId);
        finalBridgeStatus.Error.Should().BeNull();
        finalBridgeStatus.Status.Should().Be("cancelled");
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

    [Theory]
    [InlineData("diagnose-high-latency")]
    [InlineData("diagnose-memory-growth")]
    [InlineData("diagnose-5xx-errors")]
    [InlineData("diagnose-slow-outbound-http")]
    [InlineData("triage-nativeaot")]
    [InlineData("diagnose-safely-in-prod")]
    public async Task GetPrompt_RendersWellFormedToolCalls_ForEveryPrompt(string promptName)
    {
        await using var client = await ConnectAsync();

        foreach (var args in new[]
        {
            (Dictionary<string, object?>?)null,
            new Dictionary<string, object?> { ["processId"] = 1234 },
        })
        {
            var result = await client.GetPromptAsync(promptName, args, cancellationToken: CancellationToken.None);
            var text = string.Join("\n", result.Messages
                .Select(m => m.Content)
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(b => b.Text));

            text.Should().NotContain(", )", $"prompt {promptName} (args={(args is null ? "null" : "pid=1234")}) must not render a trailing comma before close-paren");
            text.Should().NotContain("(,", $"prompt {promptName} (args={(args is null ? "null" : "pid=1234")}) must not render a leading comma after open-paren");
            text.Should().NotContain(",,", $"prompt {promptName} (args={(args is null ? "null" : "pid=1234")}) must not render a double comma");
            text.Should().NotContain("{{", $"prompt {promptName} (args={(args is null ? "null" : "pid=1234")}) must not leak unescaped interpolation placeholders");
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
    public async Task CollectActivities_CapturesGeneratedActivitySourceEvents()
    {
        await using var client = await ConnectAsync();

        var driver = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var parent = IntegrationActivitySource.StartActivity("integration-parent");
            parent?.SetTag("component", "tests");
            await Task.Delay(30);
            using var child = IntegrationActivitySource.StartActivity("integration-child");
            child?.SetTag("db.system", "fake");
            await Task.Delay(20);
        });

        var result = await client.CallToolAsync(
            "collect_activities",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["sources"] = new[] { IntegrationActivitySource.Name },
                ["durationSeconds"] = 3,
                ["maxActivities"] = 20,
            },
            cancellationToken: CancellationToken.None);

        await driver;

        result.IsError.Should().NotBe(true);
        var capture = DeserializeStructured<ActivityCapture>(result);
        capture.Should().NotBeNull();
        capture!.SourceFilters.Should().ContainSingle().Which.Should().Be(IntegrationActivitySource.Name);
        capture.BySource.Should().Contain(summary => summary.SourceName == IntegrationActivitySource.Name);
        capture.ByOperation.Should().Contain(summary => summary.SourceName == IntegrationActivitySource.Name && summary.OperationName == "integration-parent");
        capture.ByOperation.Should().Contain(summary => summary.SourceName == IntegrationActivitySource.Name && summary.OperationName == "integration-child");
        capture.Activities.Should().Contain(activity => activity.SourceName == IntegrationActivitySource.Name && activity.OperationName == "integration-parent");
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

        // Sandbox (issue #163): outputDirectory must be relative — the server resolves it
        // under the operator-configured artifact root (MCP_ARTIFACT_ROOT, default
        // {temp}/dotnet-diagnostics-mcp).
        var relativeSub = $"diagnosticsmcp-tests-{Guid.NewGuid():N}";
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp", relativeSub);
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = relativeSub,
                },
                cancellationToken: CancellationToken.None);

            result.IsError.Should().NotBe(true);
            var dump = DeserializeStructured<DumpResult>(result);
            dump.Should().NotBeNull();
            dump!.ProcessId.Should().Be(Environment.ProcessId);
            dump.FilePath.Should().StartWith(absoluteRoot);
            File.Exists(dump.FilePath).Should().BeTrue();
            dump.FileSizeBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(absoluteRoot))
                {
                    Directory.Delete(absoluteRoot, recursive: true);
                }
            }
            catch (Exception)
            {
                // best effort cleanup
            }
        }
    }

    [Fact]
    public async Task CollectProcessDump_RejectsAbsoluteOutputDirectory()
    {
        await using var client = await ConnectAsync();

        var absolute = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-escape-{Guid.NewGuid():N}");
        var result = await client.CallToolAsync(
            "collect_process_dump",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["dumpType"] = "Mini",
                ["outputDirectory"] = absolute,
            },
            cancellationToken: CancellationToken.None);

        // The envelope itself does not flip IsError (structured-error contract); the
        // failure is carried in the typed payload's Error.Kind so the LLM can branch.
        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArtifactPath");
        Directory.Exists(absolute).Should().BeFalse("rejected paths must never be created");
    }

    [Fact]
    public async Task GetContainerSignals_RunsAgainstSelfHost()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_container_signals",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var signals = DeserializeStructured<ContainerSignals>(result);
        signals.Should().NotBeNull();
        signals!.ProcessId.Should().Be(Environment.ProcessId);
        signals.Notes.Should().NotBeNull();
        // Behavior is platform-dependent: Linux test runners may or may not be in a container
        // and may be on cgroup v1 or v2 — the only invariant is that the envelope deserializes
        // and the tool surfaces partial results via the Notes contract.
    }

    [Fact]
    public async Task QueryCollection_ReturnsHandleExpiredErrorForUnknownHandle()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "query_collection",
            new Dictionary<string, object?>
            {
                ["handle"] = "DEADBEEFDEADBEEFDEAD",
                ["view"] = "summary",
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("an unknown handle must surface a structured DiagnosticError");
        envelope.Error!.Kind.Should().Be("HandleExpired");
        envelope.Hints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task QueryCollection_DrillsIntoCollectExceptionsHandle()
    {
        await using var client = await ConnectAsync();

        var collectResult = await client.CallToolAsync(
            "collect_exceptions",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
                ["maxRecent"] = 10,
            },
            cancellationToken: CancellationToken.None);

        collectResult.IsError.Should().NotBe(true);
        var collectEnvelope = DeserializeEnvelope(collectResult);
        collectEnvelope!.Handle.Should().NotBeNullOrWhiteSpace(
            "every windowed collector must emit a handle so query_collection can drill (issue #43)");

        var queryResult = await client.CallToolAsync(
            "query_collection",
            new Dictionary<string, object?>
            {
                ["handle"] = collectEnvelope.Handle!,
                ["view"] = "byType",
                ["topN"] = 25,
            },
            cancellationToken: CancellationToken.None);

        queryResult.IsError.Should().NotBe(true);
        var queried = DeserializeStructured<CollectionQueryResult>(queryResult);
        queried.Should().NotBeNull();
        queried!.Kind.Should().Be(CollectionHandleKinds.ExceptionSnapshot);
        queried.View.Should().Be("byType");
        queried.ProcessId.Should().Be(Environment.ProcessId);
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
    public async Task CollectCpuSample_RunAsJob_HandleSurvivesAndPollableUntilCompleted()
    {
        // Regression test for dogfood issue #62 "handle dies within seconds".
        // Reality check: the runAsJob handle MUST be poll-able from the moment
        // collect_cpu_sample returns the ack, through job execution, until well
        // past completion. If get_collection_status ever returns HandleNotFound
        // while the job is alive, the LLM polling loop is irrecoverable.
        await using var client = await ConnectAsync();

        var startResult = await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
                ["topN"] = 5,
                ["resolveSourceLines"] = false,
                ["runAsJob"] = true,
            },
            cancellationToken: CancellationToken.None);

        startResult.IsError.Should().NotBe(true, "starting the job must succeed");
        var startEnvelope = DeserializeEnvelope(startResult);
        startEnvelope.Should().NotBeNull();
        startEnvelope!.Error.Should().BeNull();
        var handle = startEnvelope.Handle;
        handle.Should().NotBeNullOrWhiteSpace("runAsJob ack must carry a handle");

        // Poll immediately — the handle must already be registered.
        var firstStatus = await PollCollectionStatusAsync(client, handle!);
        firstStatus.Error.Should().BeNull(
            "the handle must be poll-able immediately after the runAsJob ack — never HandleNotFound");
        firstStatus.Status.Should().BeOneOf("running", "completed",
            "expected an in-flight or already-finished job, not an evicted handle");

        // Poll to terminal. Job is 2s, give it ample headroom.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        CollectionStatusReport? terminal = null;
        while (DateTime.UtcNow < deadline)
        {
            var report = await PollCollectionStatusAsync(client, handle!);
            report.Error.Should().BeNull(
                "the handle must remain poll-able for the full job lifetime — dogfood #62");
            if (report.Status is "completed" or "failed" or "canceled")
            {
                terminal = report;
                break;
            }

            await Task.Delay(500);
        }

        terminal.Should().NotBeNull("the job must reach a terminal state within the timeout");
        terminal!.Status.Should().Be("completed",
            "a CPU sample against a healthy live process should complete cleanly");

        // And the handle must still be poll-able AFTER completion (10min handle TTL).
        var postTerminal = await PollCollectionStatusAsync(client, handle!);
        postTerminal.Error.Should().BeNull(
            "completed job handles must survive past the job to let the LLM retrieve results");
        postTerminal.Status.Should().Be("completed");
    }

    private async Task<CollectionStatusReport> PollCollectionStatusAsync(McpClient client, string handle)
    {
        var pollResult = await client.CallToolAsync(
            "get_collection_status",
            new Dictionary<string, object?> { ["handle"] = handle },
            cancellationToken: CancellationToken.None);

        // The schema-validation guard from #61 also lives here implicitly: if we ever
        // re-introduce the nullable-required bug, McpClient throws before we get here.
        pollResult.IsError.Should().NotBe(true,
            "get_collection_status must not surface tool-level errors for an alive handle");

        var envelope = DeserializeEnvelope(pollResult);
        envelope.Should().NotBeNull();
        if (envelope!.Error is not null)
        {
            // Surface the error envelope to the caller so the test can assert on it.
            return new CollectionStatusReport(
                Handle: handle,
                Kind: string.Empty,
                ProcessId: 0,
                Status: "error",
                StartedAt: default,
                CompletedAt: null,
                ElapsedSeconds: 0,
                Result: null,
                Error: envelope.Error);
        }

        var data = envelope.Data;
        return new CollectionStatusReport(
            Handle: data.GetProperty("handle").GetString()!,
            Kind: data.GetProperty("kind").GetString()!,
            ProcessId: data.GetProperty("processId").GetInt32(),
            Status: data.GetProperty("status").GetString()!,
            StartedAt: data.GetProperty("startedAt").GetDateTimeOffset(),
            CompletedAt: data.TryGetProperty("completedAt", out var ca) && ca.ValueKind != JsonValueKind.Null
                ? ca.GetDateTimeOffset()
                : null,
            ElapsedSeconds: data.GetProperty("elapsedSeconds").GetDouble(),
            Result: data.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? (object)r : null,
            Error: null);
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
    public async Task StartInvestigation_BogusPid_ReturnsStructuredProcessNotFoundError()
    {
        // Regression for #72. Before the fix `start_investigation(processId=99999999)`
        // returned a 200 with a partial `resolvedProcess` envelope that was missing
        // the schema-required `runtimeVersion` field (because CapabilityDetector returns
        // a blank DiagnosticCapabilities for non-existent PIDs and the SDK omits null
        // values on the wire — same nullable-required family as #61/#70). Strict clients
        // rejected the response and the LLM never learned the target was gone.
        // The fix is two-pronged: (1) fail-fast in ProcessContextResolver when the PID
        // is not running, returning a structured ProcessNotFound error, AND (2) make
        // `ProcessContext.RuntimeVersion` schema-honest (default null) so any future
        // partial-context path can't trip strict clients again.
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "start_investigation",
            new Dictionary<string, object?> { ["processId"] = 99999999 },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("non-existent PID must surface a structured error, not a partial resolvedProcess");
        envelope.Error!.Kind.Should().Be("ProcessNotFound");
        envelope.ResolvedProcess.Should().BeNull("no ProcessContext should be attached when the PID does not exist");
    }

    [Fact]
    public async Task StartInvestigation_OutputSchema_ResolvedProcessRuntimeVersionIsOptional()
    {
        // Defence-in-depth for #72. Even with the fail-fast guard, ProcessContext is a
        // shared shape attached to every diagnostic response — the schema must honestly
        // mark `runtimeVersion` as optional (nullable + default) so any code path that
        // legitimately ships a context with a null version (e.g. an old runtime that
        // doesn't expose ClrProductVersionString) doesn't break strict clients.
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var start = tools.Single(t => t.Name == "start_investigation");
        var schema = start.ReturnJsonSchema!.Value.GetProperty("properties");
        if (schema.TryGetProperty("resolvedProcess", out var resolved) &&
            resolved.TryGetProperty("required", out var requiredArr))
        {
            var required = requiredArr.EnumerateArray().Select(e => e.GetString()!).ToArray();
            required.Should().NotContain("runtimeVersion",
                "ProcessContext.RuntimeVersion is nullable and must not appear in `required` (#72)");
        }
    }

    [Fact]
    public async Task StartInvestigation_OutputSchema_DoesNotMarkNullablesAsRequired()
    {
        // Regression for #70 (same family as #61). `InvestigationPlan` exposes nullable
        // primary-ctor params (Symptom, Hypothesis, Baseline, BaselineComparisons). The
        // SDK serializes structured tool output with JsonIgnoreCondition.WhenWritingNull,
        // so when those values are null the wire payload omits them — but the JSON Schema
        // generator only treats a param as optional if it has an explicit default value.
        // The user-visible failure was: `start_investigation(processId, symptom="dogfood")`
        //   → McpError -32602: data/data must have required property 'hypothesis',
        //     data/data must have required property 'baseline'.
        // Fix: reorder the InvestigationPlan record so nullable params come after required
        // ones and carry `= null` defaults.
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var start = tools.Single(t => t.Name == "start_investigation");
        var dataSchema = start.ReturnJsonSchema!.Value.GetProperty("properties").GetProperty("data");
        var dataRequired = dataSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        dataRequired.Should().NotContain(new[] { "symptom", "hypothesis", "baseline", "baselineComparisons" },
            "nullable properties must NOT be in `required` — the SDK omits null values on the wire (#70)");
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
    public async Task GetCollectionStatus_UnknownHandle_PassesMcpOutputSchemaValidation()
    {
        // Regression for #61: the SDK validates structured tool output against the declared
        // outputSchema. The client surfaced `McpError -32602: Structured content does not
        // match the tool's output schema: data/data must have required property 'error'`
        // because the JsonSchemaExporter marked nullable parameters of `CollectionStatusReport`
        // (CompletedAt, Result, Error) as required, while the SDK serializes with
        // JsonIgnoreCondition.WhenWritingNull and omitted them from the wire. Fix: add
        // `= null` defaults to those parameters so the generator drops them from `required`.
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_collection_status",
            new Dictionary<string, object?> { ["handle"] = "DEADBEEFDEADBEEFDEAD" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("an unknown handle must surface a structured DiagnosticError");
        envelope.Error!.Kind.Should().Be("HandleNotFound");
        envelope.Hints.Should().NotBeEmpty();

        // Belt-and-suspenders: assert the declared outputSchema does NOT require nullable
        // properties that the serializer will omit. Catches future regressions where someone
        // drops the `= null` default on a primary-ctor param of CollectionStatusReport.
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var status = tools.Single(t => t.Name == "get_collection_status");
        var dataSchema = status.ReturnJsonSchema!.Value.GetProperty("properties").GetProperty("data");
        var dataRequired = dataSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        dataRequired.Should().NotContain(new[] { "completedAt", "result", "error" },
            "nullable properties must NOT be in `required` — the SDK omits null values on the wire (issue #61)");
    }

    [Fact]
    public async Task GetCollectionStatus_RunningJob_PassesMcpOutputSchemaValidation()
    {
        // Regression for #61: when a real background job is in 'Running' or 'Completed'
        // state, polling get_collection_status used to fail with McpError -32602
        // "data/data must have required property 'error'" because the SDK's schema generator
        // emits a required `error` property on the inner envelope wrapped inside
        // `CollectionStatusReport.Result`. The MCP client throws before the response reaches
        // the caller, so simply receiving a CallToolResult without exception proves the fix.
        await using var client = await ConnectAsync();

        var startResult = await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["runAsJob"] = true,
            },
            cancellationToken: CancellationToken.None);

        startResult.IsError.Should().NotBe(true);
        var startEnvelope = DeserializeEnvelope(startResult);
        startEnvelope!.Handle.Should().NotBeNullOrWhiteSpace("runAsJob=true must return a job handle");

        // Poll while Running. The act of receiving the CallToolResult without the MCP
        // client throwing McpError -32602 is the assertion.
        var pollResult = await client.CallToolAsync(
            "get_collection_status",
            new Dictionary<string, object?> { ["handle"] = startEnvelope.Handle! },
            cancellationToken: CancellationToken.None);
        pollResult.IsError.Should().NotBe(true,
            "polling a Running job must not violate the outputSchema (issue #61)");

        // Wait for completion and poll again — the Completed path embeds the full
        // DiagnosticResult<CpuSample> inside Result and exercised a different code path
        // in the original repro.
        await Task.Delay(TimeSpan.FromSeconds(5));
        var finalResult = await client.CallToolAsync(
            "get_collection_status",
            new Dictionary<string, object?> { ["handle"] = startEnvelope.Handle! },
            cancellationToken: CancellationToken.None);
        finalResult.IsError.Should().NotBe(true,
            "polling a Completed job must not violate the outputSchema (issue #61)");
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

    [Fact]
    public async Task GetMemoryTrend_RejectsInvalidDuration()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_memory_trend",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 1, // < 2, must be rejected
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull("durationSeconds < 2 must surface a structured DiagnosticError");
        envelope.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task GetMemoryTrend_ReturnsTrendWithSamplesAndVerdict()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "get_memory_trend",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 4,
                ["sampleEverySeconds"] = 1,
            },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBe(true);
        var trend = DeserializeStructured<DotnetDiagnosticsMcp.Core.Memory.MemoryTrend>(result);
        trend.Should().NotBeNull();
        trend!.ProcessId.Should().Be(Environment.ProcessId);
        trend.Samples.Should().HaveCountGreaterThanOrEqualTo(2, "a 4s window with 1s interval must yield at least 2 samples");
        trend.Verdict.Should().BeOneOf("growing", "stable", "shrinking");
        trend.Deltas.Should().NotBeNull();
        trend.Samples.Should().AllSatisfy(s =>
        {
            s.RssBytes.Should().BeGreaterThan(0, "RSS must be positive for a running process");
        });
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
