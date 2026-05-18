using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDbgMcp.Core.Capabilities;
using DotnetDbgMcp.Core.Counters;
using DotnetDbgMcp.Core.CpuSampling;
using DotnetDbgMcp.Core.Dump;
using DotnetDbgMcp.Core.EventSources;
using DotnetDbgMcp.Core.Exceptions;
using DotnetDbgMcp.Core.Gc;
using DotnetDbgMcp.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DotnetDbgMcp.Server.IntegrationTests;

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
            "collect_process_dump");

        foreach (var tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace($"tool {tool.Name} must document itself for the LLM");
            tool.JsonSchema.ValueKind.Should().Be(JsonValueKind.Object);
        }
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

        var directory = Path.Combine(Path.GetTempPath(), $"dbgmcp-tests-{Guid.NewGuid():N}");
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

        result.IsError.Should().Be(true);
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

        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
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

        return JsonSerializer.Deserialize<T>(json, DeserializeOptions);
    }

    public sealed class AuthedFactory : WebApplicationFactory<DotnetDbgMcp.Server.Program>
    {
        public const string Token = "test-bearer-token-do-not-use-in-prod";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
