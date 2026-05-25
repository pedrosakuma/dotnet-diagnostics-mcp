using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Artifacts;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class ByteFetchToolsTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _sampleDll;

    private int SampleProcessId => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");
    private string SampleDll => _sampleDll ?? throw new InvalidOperationException("Sample DLL not resolved.");

    [Fact]
    public async Task GetModuleBytes_MissingScope_IsRejectedByAuthorizationFilter()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("counters-only", "scope-miss-token", new[] { "read-counters" }));
        await using var client = await ConnectWithTokenAsync(factory, "scope-miss-token");

        var mvid = GetSampleMvid();
        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = mvid, ["processId"] = SampleProcessId },
            cancellationToken: CancellationToken.None);

        var (_, envelope) = ParseForbidden(result);
        envelope.GetProperty("kind").GetString().Should().Be("forbidden");
        envelope.GetProperty("required_scopes").EnumerateArray().Select(e => e.GetString())
            .Should().ContainSingle().Which.Should().Be("module-bytes-read");
    }

    [Fact]
    public async Task GetModuleBytes_RootTokenWithoutExplicitModifier_IsRejected()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("root-only", "root-token", new[] { "*" }));
        await using var client = await ConnectWithTokenAsync(factory, "root-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = GetSampleMvid(), ["processId"] = SampleProcessId },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBeTrue();
        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("Forbidden");
        envelope.Error.Message.Should().Contain("module-bytes-read");
    }

    [Fact]
    public async Task GetModuleBytes_WithExplicitScope_Succeeds()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "module-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "module-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = GetSampleMvid(), ["processId"] = SampleProcessId, ["maxBytes"] = 512 },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().BeNull();
        var data = envelope.Data;
        data.GetProperty("asset").GetString().Should().Be("pe");
        Convert.FromBase64String(data.GetProperty("base64Chunk").GetString()!).Take(2).Should().Equal((byte)'M', (byte)'Z');
    }

    [Fact]
    public async Task GetDumpBytes_RejectsPathTraversal()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "dump-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "dump-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "dump", ["dumpFilePath"] = "../escape.dmp" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArtifactPath");
    }

    [Fact]
    public async Task GetDumpBytes_RoundTripsDumpContent()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("byte-fetcher", "roundtrip-token", new[] { "module-bytes-read", "dump-write", "ptrace" }));
        await using var client = await ConnectWithTokenAsync(factory, "roundtrip-token");

        var dumpResult = await client.CallToolAsync(
            "collect_process_dump",
            new Dictionary<string, object?>
            {
                ["processId"] = SampleProcessId,
                ["dumpType"] = ProcessDumpType.Mini.ToString(),
                ["confirm"] = true,
            },
            cancellationToken: CancellationToken.None);

        var dumpEnvelope = DeserializeEnvelope(dumpResult);
        dumpEnvelope.Should().NotBeNull();
        dumpEnvelope!.Error.Should().BeNull();
        var dumpFilePath = dumpEnvelope.Data.GetProperty("dump").GetProperty("filePath").GetString();
        dumpFilePath.Should().NotBeNullOrWhiteSpace();

        var bytes = new List<byte>();
        long offset = 0;
        string? sha256 = null;
        while (true)
        {
            var chunkResult = await client.CallToolAsync(
                "get_bytes",
                new Dictionary<string, object?>
                {
                    ["kind"] = "dump",
                    ["dumpFilePath"] = dumpFilePath!,
                    ["offset"] = offset,
                    ["maxBytes"] = 1024 * 1024,
                },
                cancellationToken: CancellationToken.None);

            var chunkEnvelope = DeserializeEnvelope(chunkResult);
            chunkEnvelope.Should().NotBeNull();
            chunkEnvelope!.Error.Should().BeNull();
            var data = chunkEnvelope.Data;
            bytes.AddRange(Convert.FromBase64String(data.GetProperty("base64Chunk").GetString()!));
            sha256 ??= data.GetProperty("sha256").GetString();
            if (!data.TryGetProperty("nextOffset", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset = next.GetInt64();
        }

        var assembled = bytes.ToArray();
        sha256.Should().Be(Convert.ToHexString(SHA256.HashData(assembled)).ToLowerInvariant());
        File.ReadAllBytes(dumpFilePath!).Should().Equal(assembled);
    }

    [Fact]
    public async Task GetDumpBytes_RejectsArtifactsOver256MiB()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        var dumpPath = Path.Combine(artifactRoot.Path, "too-large.dmp");
        await using (var stream = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(FileChunkReader.MaxArtifactBytes + 1);
        }

        await using var factory = CreateFactory(("module-reader", "ceiling-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "ceiling-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "dump", ["dumpFilePath"] = dumpPath },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        envelope.Error.Message.Should().Contain("256 MiB");
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

    private static (string Summary, JsonElement Envelope) ParseForbidden(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var splitIndex = text.IndexOf('\n');
        splitIndex.Should().BeGreaterThan(0);
        var summary = text[..splitIndex];
        var json = text[(splitIndex + 1)..];
        var envelope = JsonDocument.Parse(json).RootElement.GetProperty("error");
        return (summary, envelope);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull();
            json = textBlock!.Text;
        }

        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
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
        var path = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(ByteFetchToolsTests), Guid.NewGuid().ToString("N"));
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
