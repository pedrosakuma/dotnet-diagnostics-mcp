using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// End-to-end tests for the <c>--health-check</c> CLI mode (issue #27). Verifies the
/// supervisor / container HEALTHCHECK contract: exit 0 when a server answers /health
/// with 2xx, exit 1 on any failure (connection refused, non-2xx, timeout).
/// </summary>
[Collection(nameof(EnvSerial))]
public class HealthCheckCommandTests
{
    [Fact]
    public void ResolveBaseUrl_DefaultsToLocalhost_WhenNoArgsOrEnv()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
        var url = HealthCheckCommand.ResolveBaseUrl([]);
        Assert.Equal("http://127.0.0.1:8787", url);
    }

    [Fact]
    public void ResolveBaseUrl_HonoursUrlsFlag_SpaceForm()
    {
        var url = HealthCheckCommand.ResolveBaseUrl(["--urls", "http://localhost:9999"]);
        Assert.Equal("http://localhost:9999", url);
    }

    [Fact]
    public void ResolveBaseUrl_HonoursUrlsFlag_EqualsForm()
    {
        var url = HealthCheckCommand.ResolveBaseUrl(["--urls=http://localhost:9999"]);
        Assert.Equal("http://localhost:9999", url);
    }

    [Fact]
    public void ResolveBaseUrl_NormalizesWildcardHosts_ToLoopback()
    {
        Assert.Equal("http://127.0.0.1:8787", HealthCheckCommand.ResolveBaseUrl(["--urls", "http://*:8787"]));
        Assert.Equal("http://127.0.0.1:8787", HealthCheckCommand.ResolveBaseUrl(["--urls", "http://+:8787"]));
        Assert.Equal("http://127.0.0.1:8787", HealthCheckCommand.ResolveBaseUrl(["--urls", "http://0.0.0.0:8787"]));
    }

    [Fact]
    public void ResolveBaseUrl_TakesFirstUrl_WhenSemicolonSeparated()
    {
        var url = HealthCheckCommand.ResolveBaseUrl(["--urls", "http://127.0.0.1:8787;https://127.0.0.1:8788"]);
        Assert.Equal("http://127.0.0.1:8787", url);
    }

    [Fact]
    public async Task RunAsync_ReturnsOne_WhenServerNotReachable()
    {
        var port = GetFreePort();
        var exit = await HealthCheckCommand.RunAsync(["--urls", $"http://127.0.0.1:{port}"], TextWriter.Null, TextWriter.Null);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task RunAsync_ReturnsZero_WhenServerAnswersHealth()
    {
        var port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        await app.StartAsync();
        try
        {
            var exit = await HealthCheckCommand.RunAsync(["--urls", $"http://127.0.0.1:{port}"], TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, exit);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
