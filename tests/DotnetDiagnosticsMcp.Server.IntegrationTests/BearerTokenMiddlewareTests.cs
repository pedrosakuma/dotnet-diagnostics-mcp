using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>Tests for the scoped <see cref="BearerTokenMiddleware"/>. Covers happy
/// path principal stamping, the structured 401 envelope, the missing-header path,
/// and an end-to-end loop through a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// configured with <c>Auth:BearerTokens</c>. Env-mutating cases live in this
/// collection so the WebApplicationFactory and registry tests don't race over
/// <c>MCP_BEARER_TOKEN</c>.</summary>
[Collection(nameof(EnvSerial))]
public sealed class BearerTokenMiddlewareTests
{
    private static BearerTokenRegistry RegistryWith(params (string Name, string Token, string[] Scopes)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"Auth:BearerTokens:{i}:Name"] = entries[i].Name;
            dict[$"Auth:BearerTokens:{i}:Token"] = entries[i].Token;
            for (var j = 0; j < entries[i].Scopes.Length; j++)
            {
                dict[$"Auth:BearerTokens:{i}:Scopes:{j}"] = entries[i].Scopes[j];
            }
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        return BearerTokenRegistry.Build(config, NullLogger.Instance, allowEphemeralFallback: true);
    }

    private static async Task<HttpContext> RunAsync(
        IPrincipalResolver resolver,
        string? authorization,
        ILogger<BearerTokenMiddleware>? logger = null,
        string path = "/mcp")
    {
        var nextCalled = false;
        var middleware = new BearerTokenMiddleware(
            ctx => { nextCalled = true; return Task.CompletedTask; },
            resolver,
            logger ?? NullLogger<BearerTokenMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (authorization is not null)
        {
            ctx.Request.Headers.Authorization = authorization;
        }

        await middleware.InvokeAsync(ctx);

        ctx.Items["__nextCalled"] = nextCalled;
        ctx.Response.Body.Position = 0;
        return ctx;
    }

    [Fact]
    public async Task ValidToken_StampsPrincipal_AndCallsNext()
    {
        var registry = RegistryWith(("ops-viewer", "tok-aaa", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, "Bearer tok-aaa");

        ((bool)ctx.Items["__nextCalled"]!).Should().BeTrue();
        var principal = ctx.GetBearerPrincipal();
        principal.Should().NotBeNull();
        principal!.Name.Should().Be("ops-viewer");
        principal.HasScope("read-counters").Should().BeTrue();
    }

    [Fact]
    public async Task MissingHeader_Returns401_WithEnvelope_AndDoesNotCallNext()
    {
        var registry = RegistryWith(("x", "tok-x", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, authorization: null);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        ctx.Response.ContentType.Should().Be("application/json");
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("kind").GetString().Should().Be("unauthenticated");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("invalid bearer token");
        ((bool)ctx.Items["__nextCalled"]!).Should().BeFalse();
        ctx.GetBearerPrincipal().Should().BeNull();
    }

    [Fact]
    public async Task BadToken_Returns401_AndDoesNotLeakTokenValue()
    {
        var registry = RegistryWith(("x", "real-tok", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));
        var logger = lf.CreateLogger<BearerTokenMiddleware>();

        const string forged = "BAD-secret-shouldnt-be-logged";
        var ctx = await RunAsync(registry, $"Bearer {forged}", logger);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ((bool)ctx.Items["__nextCalled"]!).Should().BeFalse();

        capture.Records.Should().NotBeEmpty();
        foreach (var record in capture.Records)
        {
            record.Message.Should().NotContain(forged, "the presented bearer must never appear in any log line");
            record.Message.Should().NotContain("real-tok", "registered token values must never appear in logs");
        }
    }

    [Fact]
    public async Task MalformedScheme_Returns401()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, "Basic tok");

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HealthPath_BypassesAuth()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, authorization: null, path: "/health");

        ((bool)ctx.Items["__nextCalled"]!).Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task AllowPath_LogsTokenName_AtInformation()
    {
        var registry = RegistryWith(("ops-viewer", "tok-aaa", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        await RunAsync(registry, "Bearer tok-aaa", lf.CreateLogger<BearerTokenMiddleware>());

        capture.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Information && r.Message.Contains("ops-viewer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DenyPath_LogsRemoteIp_AndMissingHeaderFlag_AtWarning()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        await RunAsync(registry, authorization: null, lf.CreateLogger<BearerTokenMiddleware>());

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.Should().Contain("missingHeader=true");
        warning.Message.Should().Contain("remoteIp=");
    }

    // ---------------------------------------------------------------------
    // End-to-end through WebApplicationFactory — exercises Program.cs wiring.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_ScopedToken_GoesThroughMiddleware()
    {
        // Each WebApplicationFactory captures Program-scope env vars at construction;
        // serialize against other env-touching tests via [Collection(EnvSerial)].
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Auth:BearerTokens:0:Name", "scoped-token");
                b.UseSetting("Auth:BearerTokens:0:Token", "scoped-secret-aaa");
                b.UseSetting("Auth:BearerTokens:0:Scopes:0", "read-counters");
            });

        using var client = factory.CreateClient();

        // No header → 401 with structured envelope
        var unauth = await client.GetAsync("/mcp");
        unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unauthBody = await unauth.Content.ReadAsStringAsync();
        unauthBody.Should().Contain("\"kind\":\"unauthenticated\"");

        // Bad token → 401
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var bad = await client.GetAsync("/mcp");
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Good token → not 401 (actual MCP handshake on GET returns whatever the SDK
        // returns; we only care that auth let it through, hence !=401).
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "scoped-secret-aaa");
        var ok = await client.GetAsync("/mcp");
        ok.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EndToEnd_LegacyEnvVar_StillWorks()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-secret-xyz");

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-secret-xyz");
        var resp = await client.GetAsync("/mcp");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var bad = await client.GetAsync("/mcp");
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void LegacyPrincipal_HasRootName_AndRootScope()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-secret");
        var registry = BearerTokenRegistry.Build(
            new ConfigurationBuilder().Build(), NullLogger.Instance, true);

        var p = registry.TryResolve("legacy-secret")!;
        p.Name.Should().Be(BearerPrincipal.LegacyRootName);
        p.Scopes.Should().Equal(new[] { BearerPrincipal.RootScope });
    }
}
