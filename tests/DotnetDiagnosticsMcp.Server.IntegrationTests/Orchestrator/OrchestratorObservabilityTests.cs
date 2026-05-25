using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.IntegrationTests;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

[Collection(nameof(EnvSerial))]
public sealed class OrchestratorObservabilityTests
{
    [Fact]
    public async Task MetricsEndpoint_DefaultsToMetricsReadScope()
    {
        await using var fx = await MetricsHost.StartAsync();

        var forbidden = await fx.GetMetricsAsync("attach-token");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var allowed = await fx.GetMetricsAsync("metrics-token");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).Should().Contain("# TYPE");
    }

    [Fact]
    public async Task MetricsEndpoint_AllowsAnonymousWhenOpenEnvSet()
    {
        using var openScope = EnvScope.Set("MCP_METRICS_OPEN", "true");
        await using var fx = await MetricsHost.StartAsync();

        var response = await fx.GetMetricsAsync(token: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("# TYPE");
    }

    [Fact]
    public async Task MetricsEndpoint_ExposesWaveOneMetricsAfterAttachProxyAndReaper()
    {
        await using var fx = await MetricsHost.StartAsync();

        var attach = await OrchestratorTools.AttachToPod(
            fx.AttachOrchestrator,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            fx.Observability,
            server: null!,
            podName: "api-0",
            containerName: "app",
            cancellationToken: CancellationToken.None);
        attach.IsError.Should().BeFalse();
        var handleId = attach.Data!.HandleId;

        fx.Binder.Bind("session-1", handleId);
        var proxyResult = await InvestigationProxyCallToolFilter.InvokeAsync(
            new CallToolRequestParams
            {
                Name = "collect_events",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["kind"] = JsonSerializer.SerializeToElement("counters"),
                },
            },
            sessionId: "session-1",
            next: (_, _) => ValueTask.FromResult(new CallToolResult()),
            fx.Binder,
            fx.Store,
            fx.ProxyClient,
            TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            fx.Observability,
            loggerAccessor: () => NullLogger.Instance,
            cancellationToken: CancellationToken.None);
        proxyResult.IsError.Should().NotBe(true);

        fx.Store.Add(new InvestigationHandle(
            HandleId: "expired-handle",
            Namespace: "ns-a",
            PodName: "api-1",
            TargetContainerName: "app",
            EphemeralContainerName: "diag-1",
            PodLocalBearerToken: "secret",
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-5)));
        await fx.Reaper.ReapExpiredAsync(DateTimeOffset.UtcNow);

        var metricsText = await (await fx.GetMetricsAsync("metrics-token")).Content.ReadAsStringAsync();
        metricsText.Should().Contain("mcp_orchestrator_attach_total");
        metricsText.Should().Contain("mcp_orchestrator_attach_duration_seconds_bucket");
        metricsText.Should().Contain("mcp_orchestrator_active_investigations");
        metricsText.Should().Contain("mcp_orchestrator_proxy_requests_total");
        metricsText.Should().Contain("mcp_orchestrator_reaper_evicted_total");
        metricsText.Should().Contain("mcp_orchestrator_attach_total{otel_scope_name=\"DotnetDiagnosticsMcp.Server.Orchestrator\",outcome=\"success\",reason=\"none\"} 1");
        metricsText.Should().Contain("mcp_orchestrator_proxy_requests_total{otel_scope_name=\"DotnetDiagnosticsMcp.Server.Orchestrator\",outcome=\"success\",tool=\"collect_events\"} 1");
        metricsText.Should().Contain("mcp_orchestrator_reaper_evicted_total{otel_scope_name=\"DotnetDiagnosticsMcp.Server.Orchestrator\",reason=\"ttl\"} 1");
    }

    [Fact]
    public async Task AuditEvents_AreWrittenAsJsonWithoutToolArguments()
    {
        using var capture = new StringWriter();
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<IInvestigationStore, MemoryInvestigationStore>();
        services.AddSingleton(new AuditLogWriter(capture));
        using var provider = services.BuildServiceProvider();

        var store = (MemoryInvestigationStore)provider.GetRequiredService<IInvestigationStore>();
        var binder = new MemoryInvestigationSessionBinder();
        var observability = new OrchestratorObservability(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            store,
            provider.GetRequiredService<AuditLogWriter>());
        var options = new OrchestratorOptions { Enabled = true, DefaultNamespace = "ns-a" };
        options.NamespaceAllowlist.Add("ns-a");
        var attachOrchestrator = new StubAttachOrchestrator(store);
        var proxyClient = new StubProxyClient();
        var closer = new InvestigationCloser(store, proxyClient, new NoOpPortForwardManager(), binder);

        var attach = await OrchestratorTools.AttachToPod(
            attachOrchestrator,
            options,
            binder,
            store,
            TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            observability,
            server: null!,
            podName: "api-0",
            containerName: "app",
            cancellationToken: CancellationToken.None);
        attach.IsError.Should().BeFalse();

        binder.Bind("audit-session", attach.Data!.HandleId);
        await InvestigationProxyCallToolFilter.InvokeAsync(
            new CallToolRequestParams
            {
                Name = "collect_events",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["processId"] = JsonDocument.Parse("null").RootElement,
                    ["secretValue"] = JsonDocument.Parse("\"do-not-log-this\"").RootElement,
                },
            },
            sessionId: "audit-session",
            next: (_, _) => ValueTask.FromResult(new CallToolResult()),
            binder,
            store,
            proxyClient,
            TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            observability,
            loggerAccessor: () => NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        var detach = await OrchestratorTools.DetachFromPod(
            closer,
            binder,
            store,
            options,
            TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            observability,
            server: null!,
            handleId: attach.Data.HandleId,
            cancellationToken: CancellationToken.None);
        detach.IsError.Should().BeFalse();

        var events = capture
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        events.Select(e => e.GetProperty("event.name").GetString()).Should().Contain(new[]
        {
            "audit.orchestrator.attach",
            "audit.orchestrator.proxy_call",
            "audit.orchestrator.detach",
        });
        capture.ToString().Should().NotContain("secretValue");
        capture.ToString().Should().NotContain("do-not-log-this");
    }

    [Fact]
    public async Task OtlpExporter_ActivatesWhenEndpointConfigured()
    {
        using var endpoint = EnvScope.Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4317");
        await using var fx = await MetricsHost.StartAsync();

        fx.Services.GetRequiredService<MeterProvider>().Should().NotBeNull();
        fx.Services.GetRequiredService<TracerProvider>().Should().NotBeNull();
        fx.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().CurrentValue.Endpoint
            .Should().Be(new Uri("http://127.0.0.1:4317"));
    }

    private sealed class MetricsHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private MetricsHost(WebApplication app, HttpClient client)
        {
            _app = app;
            _client = client;
        }

        public MemoryInvestigationStore Store => (MemoryInvestigationStore)_app.Services.GetRequiredService<IInvestigationStore>();
        public MemoryInvestigationSessionBinder Binder => _app.Services.GetRequiredService<MemoryInvestigationSessionBinder>();
        public OrchestratorObservability Observability => _app.Services.GetRequiredService<OrchestratorObservability>();
        public OrchestratorOptions Options => _app.Services.GetRequiredService<OrchestratorOptions>();
        public InvestigationHandleReaperBackgroundService Reaper => _app.Services.GetRequiredService<InvestigationHandleReaperBackgroundService>();
        public StubAttachOrchestrator AttachOrchestrator => _app.Services.GetRequiredService<StubAttachOrchestrator>();
        public StubProxyClient ProxyClient => _app.Services.GetRequiredService<StubProxyClient>();
        public IServiceProvider Services => _app.Services;

        public static async Task<MetricsHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:BearerTokens:0:Name"] = "metrics-reader",
                ["Auth:BearerTokens:0:Token"] = "metrics-token",
                ["Auth:BearerTokens:0:Scopes:0"] = "metrics-read",
                ["Auth:BearerTokens:1:Name"] = "attach-only",
                ["Auth:BearerTokens:1:Token"] = "attach-token",
                ["Auth:BearerTokens:1:Scopes:0"] = "orchestrator-attach",
            });

            builder.Services.AddRouting();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IPrincipalAccessor, HttpContextPrincipalAccessor>();
            builder.Services.AddSingleton<IInvestigationStore, MemoryInvestigationStore>();
            builder.Services.AddSingleton<MemoryInvestigationSessionBinder>();
            builder.Services.AddSingleton<IInvestigationSessionBinder>(sp => sp.GetRequiredService<MemoryInvestigationSessionBinder>());
            builder.Services.AddSingleton<StubProxyClient>();
            builder.Services.AddSingleton<IInvestigationProxyClient>(sp => sp.GetRequiredService<StubProxyClient>());
            builder.Services.AddSingleton<IPortForwardManager, NoOpPortForwardManager>();
            builder.Services.AddSingleton<InvestigationCloser>();
            builder.Services.AddSingleton<InvestigationHandleReaperBackgroundService>();
            builder.Services.AddSingleton<StubAttachOrchestrator>();
            builder.Services.AddSingleton<OrchestratorOptions>(_ =>
            {
                var options = new OrchestratorOptions
                {
                    Enabled = true,
                    DefaultNamespace = "ns-a",
                    ProxyBasePath = "/proxy",
                };
                options.NamespaceAllowlist.Add("ns-a");
                return options;
            });
            builder.AddOrchestratorObservability(orchestratorEnabled: true);

            var app = builder.Build();
            var registry = BearerTokenRegistry.Build(builder.Configuration, NullLogger.Instance, allowEphemeralFallback: true);
            app.UseMiddleware<BearerTokenMiddleware>((IPrincipalResolver)registry, OidcJwtAuthOptions.Disabled);
            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
            app.MapOrchestratorObservability();
            await app.StartAsync();
            return new MetricsHost(app, app.GetTestClient());
        }

        public async Task<HttpResponseMessage> GetMetricsAsync(string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubAttachOrchestrator : IPodAttachOrchestrator
    {
        private readonly MemoryInvestigationStore _store;
        private int _nextId = 1;

        public StubAttachOrchestrator(IInvestigationStore store)
        {
            _store = (MemoryInvestigationStore)store;
        }

        public Task<InvestigationHandle> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
        {
            var handle = new InvestigationHandle(
                HandleId: $"inv-{_nextId++}",
                Namespace: string.IsNullOrWhiteSpace(request.Namespace) ? "ns-a" : request.Namespace,
                PodName: request.PodName,
                TargetContainerName: request.ContainerName ?? "app",
                EphemeralContainerName: "diag-1",
                PodLocalBearerToken: "pod-secret",
                State: InvestigationState.Active,
                AttachedAt: DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                OwnerSessionId: request.OwnerSessionId);
            _store.Add(handle);
            return Task.FromResult(handle);
        }
    }

    private sealed class StubProxyClient : IInvestigationProxyClient
    {
        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
            => Task.FromResult(new CallToolResult());

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class NoOpPortForwardManager : IPortForwardManager
    {
        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }
}
