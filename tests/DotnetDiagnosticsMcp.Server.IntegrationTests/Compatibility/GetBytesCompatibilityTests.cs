using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Artifacts;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.4 — verifies that the new <c>get_bytes(kind=...)</c> tool returns
/// envelopes structurally equal to the legacy <c>get_module_bytes</c> /
/// <c>get_dump_bytes</c> entrypoints it consolidates. Uses
/// <see cref="CompatibilityEnvelopeAssert"/> so the contract is asserted on the
/// wire shape (CallTool JSON), not on internal types.
/// </summary>
/// <remarks>
/// <para>The successor delegates to the legacy implementations during the deprecation
/// window, so the envelopes are expected to be byte-for-byte identical (no fields
/// masked). If a future change re-routes the new tool through a separate code path
/// — e.g. emitting <c>NextActionHint.tool = "get_bytes"</c> — this test should be
/// updated to mask only those fields via <c>CompatibilityIgnore.Paths(...)</c>.</para>
/// </remarks>
[Collection(nameof(EnvSerial))]
public sealed class GetBytesCompatibilityTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _sampleDll;

    private int SampleProcessId => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");
    private string SampleDll => _sampleDll ?? throw new InvalidOperationException("Sample DLL not resolved.");

    [Fact]
    public async Task GetBytes_KindModule_MatchesGetModuleBytes()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "module-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "module-token");

        var mvid = GetSampleMvid();
        var moduleArgs = new Dictionary<string, object?>
        {
            ["moduleVersionId"] = mvid,
            ["processId"] = SampleProcessId,
            ["maxBytes"] = 512,
        };

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => CallToolJsonAsync(client, "get_module_bytes", moduleArgs),
            successor: () => CallToolJsonAsync(client, "get_bytes",
                new Dictionary<string, object?>(moduleArgs) { ["kind"] = "module" }));
    }

    [Fact]
    public async Task GetBytes_KindDump_MatchesGetDumpBytes()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);

        // Materialise a small synthetic dump file; the byte-streaming envelope is
        // payload-agnostic so we don't need a real WithHeap dump for shape-equality.
        var dumpPath = Path.Combine(artifactRoot.Path, "compat.dmp");
        await File.WriteAllBytesAsync(dumpPath, Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xFF)).ToArray());

        await using var factory = CreateFactory(("module-reader", "dump-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "dump-token");

        var dumpArgs = new Dictionary<string, object?>
        {
            ["dumpFilePath"] = dumpPath,
            ["maxBytes"] = 512,
        };

        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => CallToolJsonAsync(client, "get_dump_bytes", dumpArgs),
            successor: () => CallToolJsonAsync(client, "get_bytes",
                new Dictionary<string, object?>(dumpArgs) { ["kind"] = "dump" }));
    }

    [Fact]
    public async Task GetBytes_UnknownKind_ReturnsStructuredInvalidArgument()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "kind-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "kind-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "nonsense" },
            cancellationToken: CancellationToken.None);

        // The discriminator helper produces a structured failure envelope (not a thrown
        // exception); the MCP filter surfaces it as a regular tool result, never as
        // IsError=true.
        result.IsError.Should().NotBeTrue();
        var json = ExtractEnvelopeJson(result);
        using var doc = JsonDocument.Parse(json);
        var err = doc.RootElement.GetProperty("error");
        err.GetProperty("kind").GetString().Should().Be("InvalidArgument");
        err.GetProperty("detail").GetString().Should().Be("kind");
        err.GetProperty("message").GetString().Should().Contain("module").And.Contain("dump");
    }

    private static async Task<JsonElement> CallToolJsonAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(
            toolName,
            arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            cancellationToken: CancellationToken.None);

        var json = ExtractEnvelopeJson(result);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string ExtractEnvelopeJson(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return structured.GetRawText();
        }

        var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        textBlock.Should().NotBeNull();
        return textBlock!.Text;
    }

    public async Task InitializeAsync()
    {
        _sampleDll = LocateSampleDll() ?? throw new InvalidOperationException("CoreClrSample.dll not found.");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_sampleDll)!,
        };
        psi.ArgumentList.Add(_sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CoreClrSample.");
        _ = DrainAsync(_sampleProcess.StandardOutput);
        _ = DrainAsync(_sampleProcess.StandardError);
        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30));
        await WaitForModuleVisibilityAsync(_sampleProcess.Id, _sampleDll, TimeSpan.FromSeconds(30));
    }

    public Task DisposeAsync()
    {
        if (_sampleProcess is { HasExited: false })
        {
            try
            {
                _sampleProcess.Kill(entireProcessTree: true);
                _sampleProcess.WaitForExit(5_000);
            }
            catch
            {
                // best effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    private string GetSampleMvid()
    {
        var mvid = new MvidReader().TryRead(SampleDll);
        mvid.Should().NotBeNull();
        return mvid!.Value.ToString("D");
    }

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

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch
        {
            // best effort
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (DiagnosticsClient.GetPublishedProcesses().Contains(processId))
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"CoreClrSample pid {processId} did not expose a diagnostic endpoint within {timeout}.");
    }

    private static async Task WaitForModuleVisibilityAsync(int processId, string sampleDll, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var mvid = new MvidReader().TryRead(sampleDll) ?? throw new InvalidOperationException("Sample DLL MVID not readable.");
        var source = new ClrMdModuleByteSource();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await source.FetchAsync(processId, mvid, asset: "pe", offset: 0, maxBytes: 2);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"CoreClrSample mvid {mvid:D} was not visible in pid {processId} within {timeout}.");
    }

    private static string? LocateSampleDll()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", "CoreClrSample");
            if (Directory.Exists(sampleDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(sampleDir, "bin", configuration, "net10.0", "CoreClrSample.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }

    private static TestDirectory CreateArtifactRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(GetBytesCompatibilityTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    private sealed class TestDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
