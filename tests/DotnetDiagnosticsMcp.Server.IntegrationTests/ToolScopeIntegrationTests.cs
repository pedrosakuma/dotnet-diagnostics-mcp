using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Server.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// B5.2 — end-to-end MCP CallTool flow with scoped bearer tokens. Verifies that the
/// authorization filter:
///  - allows the call when the principal holds the required scope;
///  - returns a structured <c>forbidden</c> envelope when it doesn't;
///  - keeps the legacy root <c>MCP_BEARER_TOKEN</c> path working byte-for-byte;
///  - emits an audit log line on each decision without leaking the bearer value.
/// Uses <see cref="WebApplicationFactory{TEntryPoint}"/>; serialized via
/// <see cref="EnvSerial"/> because the factory captures Program-scope env vars.
/// </summary>
[Collection(nameof(EnvSerial))]
public sealed class ToolScopeIntegrationTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string Name, string Token, string[] Scopes)[] tokens)
    {
        Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", null);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                b.UseSetting($"Auth:BearerTokens:{i}:Name", tokens[i].Name);
                b.UseSetting($"Auth:BearerTokens:{i}:Token", tokens[i].Token);
                for (var j = 0; j < tokens[i].Scopes.Length; j++)
                {
                    b.UseSetting($"Auth:BearerTokens:{i}:Scopes:{j}", tokens[i].Scopes[j]);
                }
            }
        });
    }

    private static async Task<McpClient> ConnectWithTokenAsync(WebApplicationFactory<Program> factory, string token)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static (string Summary, JsonElement Envelope) ParseForbidden(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var splitIndex = text.IndexOf('\n');
        splitIndex.Should().BeGreaterThan(0, "forbidden text payload should be '<summary>\\n<json>'");
        var summary = text[..splitIndex];
        var json = text[(splitIndex + 1)..];
        var envelope = JsonDocument.Parse(json).RootElement.GetProperty("error");
        return (summary, envelope);
    }

    [Fact]
    public async Task ScopedToken_With_Matching_Scope_Allows_Call()
    {
        await using var factory = CreateFactory(
            ("counters-only", "counters-secret-aaa", new[] { "read-counters" }));
        await using var client = await ConnectWithTokenAsync(factory, "counters-secret-aaa");

        // list_dotnet_processes is read-counters; no target attach needed, so the call
        // returns a real success envelope. We only care that it was NOT rejected by the
        // scope filter (which would surface as IsError=true with kind=forbidden).
        var result = await client.CallToolAsync(
            "list_dotnet_processes",
            arguments: new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        // Either the tool succeeded or it failed for a non-scope reason — neither path
        // produces our forbidden envelope.
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
        text.Should().NotContain("\"kind\":\"forbidden\"");
    }

    [Fact]
    public async Task ScopedToken_Missing_Scope_Returns_Forbidden_Envelope()
    {
        await using var factory = CreateFactory(
            ("counters-only", "counters-secret-bbb", new[] { "read-counters" }));
        await using var client = await ConnectWithTokenAsync(factory, "counters-secret-bbb");

        // collect_cpu_sample requires 'eventpipe'; the counters-only token does not.
        var result = await client.CallToolAsync(
            "collect_cpu_sample",
            arguments: new Dictionary<string, object?> { ["durationSeconds"] = 1 },
            cancellationToken: CancellationToken.None);

        var (summary, envelope) = ParseForbidden(result);
        summary.Should().StartWith("forbidden: tool 'collect_cpu_sample'");
        envelope.GetProperty("kind").GetString().Should().Be("forbidden");
        envelope.GetProperty("tool").GetString().Should().Be("collect_cpu_sample");
        envelope.GetProperty("required_scopes").EnumerateArray()
            .Select(e => e.GetString()).Should().ContainSingle()
            .Which.Should().Be("eventpipe");
        envelope.GetProperty("principal_scopes").EnumerateArray()
            .Select(e => e.GetString()).Should().ContainSingle()
            .Which.Should().Be("read-counters");
        envelope.GetProperty("semantics").GetString().Should().Be("all");
    }

    [Fact]
    public async Task Stacked_Scope_Denies_When_Only_One_Is_Held()
    {
        // collect_process_dump stacks 'ptrace' + 'dump-write'. A token with only 'ptrace'
        // must miss the second scope explicitly.
        await using var factory = CreateFactory(
            ("ptrace-only", "ptrace-secret-ccc", new[] { "ptrace" }));
        await using var client = await ConnectWithTokenAsync(factory, "ptrace-secret-ccc");

        var result = await client.CallToolAsync(
            "collect_process_dump",
            arguments: new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        var (_, envelope) = ParseForbidden(result);
        envelope.GetProperty("required_scopes").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("dump-write", "ptrace");
    }

    [Fact]
    public async Task RequireAnyScope_Allows_When_Principal_Holds_Either_Candidate()
    {
        await using var factory = CreateFactory(
            ("counters-only", "counters-secret-ddd", new[] { "read-counters" }),
            ("eventpipe-only", "eventpipe-secret-eee", new[] { "eventpipe" }));

        foreach (var token in new[] { "counters-secret-ddd", "eventpipe-secret-eee" })
        {
            await using var client = await ConnectWithTokenAsync(factory, token);

            // query_collection uses RequireAnyScope("read-counters", "eventpipe").
            // We supply a bogus handle so the tool body returns a HandleExpired error,
            // but the scope filter must let the call through either way.
            var result = await client.CallToolAsync(
                "query_collection",
                arguments: new Dictionary<string, object?> { ["handle"] = "bogus" },
                cancellationToken: CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
            text.Should().NotContain("\"kind\":\"forbidden\"", $"token '{token}' should pass the OR gate");
        }
    }

    [Fact]
    public async Task LegacyRootToken_Continues_To_Satisfy_Every_Scope()
    {
        // MCP_BEARER_TOKEN → synthesized 'legacy-root' principal with scope {"root"}.
        // RFC 0001 §2.13 / B5.2: root wildcard must satisfy every per-tool [RequireScope]
        // gate so existing deployments keep working byte-for-byte.
        Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", "legacy-root-secret-fff");
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var client = await ConnectWithTokenAsync(factory, "legacy-root-secret-fff");

            // Pick a scope-stacked tool to maximise the test signal.
            var result = await client.CallToolAsync(
                "collect_process_dump",
                arguments: new Dictionary<string, object?>(),
                cancellationToken: CancellationToken.None);

            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
            text.Should().NotContain("\"kind\":\"forbidden\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", null);
        }
    }

    [Fact]
    public async Task AuditLog_OnDeny_DoesNotLeak_BearerValue()
    {
        var logSink = new ListLoggerProvider();
        await using var factory = CreateFactory(
            ("counters-only", "verysecret-do-not-log-ggg", new[] { "read-counters" }))
            .WithWebHostBuilder(b => b.ConfigureLogging(lb =>
            {
                lb.SetMinimumLevel(LogLevel.Information);
                lb.AddProvider(logSink);
            }));

        await using var client = await ConnectWithTokenAsync(factory, "verysecret-do-not-log-ggg");
        _ = await client.CallToolAsync(
            "collect_cpu_sample",
            arguments: new Dictionary<string, object?> { ["durationSeconds"] = 1 },
            cancellationToken: CancellationToken.None);

        var denyLines = logSink.Records
            .Where(r => r.Message.Contains("denied for principal", StringComparison.Ordinal))
            .ToList();
        denyLines.Should().NotBeEmpty("the filter must emit a warning audit line on deny");
        denyLines.Should().AllSatisfy(r =>
        {
            r.Level.Should().Be(LogLevel.Warning);
            r.Message.Should().NotContain("verysecret-do-not-log-ggg",
                "bearer values must never appear in audit logs (RFC 0001 §8)");
        });
        // Belt-and-braces: scan the entire log buffer for the bearer string.
        logSink.Records.Should().NotContain(r => r.Message.Contains("verysecret-do-not-log-ggg", StringComparison.Ordinal));
    }
}
