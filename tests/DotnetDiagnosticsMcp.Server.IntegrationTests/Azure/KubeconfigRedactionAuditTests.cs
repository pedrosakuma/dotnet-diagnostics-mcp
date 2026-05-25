using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Security regression: asserts that the kubeconfig YAML bytes minted by the
/// fake AKS adapter NEVER appear in either the discover_azure response envelope
/// or any structured log line emitted during the call (#234).
/// </summary>
public sealed class KubeconfigRedactionAuditTests
{
    /// <summary>Distinctive sentinels we plant inside the fake kubeconfig — none of
    /// these may appear in the JSON response or in captured log output.</summary>
    private const string ServerSentinel = "https://example-aks-SUPERSECRETSENTINEL.hcp.eastus.azmk8s.io";

    private const string TokenSentinel = "eyJSENTINELTOKENJWTPAYLOAD.0123456789ABCDEF";

    private static readonly byte[] KubeconfigPayload = Encoding.UTF8.GetBytes(
        "apiVersion: v1\nkind: Config\nclusters:\n- cluster:\n    server: " + ServerSentinel +
        "\nusers:\n- user:\n    token: " + TokenSentinel + "\n");

    [Fact]
    public async Task DiscoverAzure_WithIncludeKubeconfig_ReturnsHandle_AndNeverSerializesRawBytes()
    {
        // ---- Arrange: capture every log line so we can sweep for the sentinels.
        var sink = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(sink).SetMinimumLevel(LogLevel.Trace));

        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) },
            clock);

        var adapter = new SentinelAdapter();
        var discovery = new AzureAksDiscovery(adapter, store, loggerFactory.CreateLogger<AzureAksDiscovery>());

        // ---- Act
        var result = await discovery.ListAsync(
            new AzureDiscoveryRequest(
                SubscriptionId: "11111111-1111-1111-1111-111111111111",
                ResourceGroup: null,
                IncludeStopped: false,
                Limit: 100,
                Cursor: null,
                IncludeKubeconfig: true),
            CancellationToken.None);

        // ---- Assert: the response carries a handle, not the kubeconfig bytes.
        result.Items.Should().HaveCount(1);
        var handoff = result.Items[0].Handoff;
        handoff.Should().NotBeNull();
        handoff!.KubeconfigHandle.Should().StartWith("kc:");

        // ---- Sweep #1 — the serialized response envelope must not contain the sentinels.
        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain(ServerSentinel);
        json.Should().NotContain(TokenSentinel);
        json.Should().NotContain("apiVersion: v1");

        // ---- Sweep #2 — the captured log lines must not contain the sentinels OR the handle value.
        var logs = sink.RenderAll();
        logs.Should().NotContain(ServerSentinel);
        logs.Should().NotContain(TokenSentinel);
        logs.Should().NotContain(handoff.KubeconfigHandle,
            because: "the handle is a bearer credential; only handle PRESENCE may be logged.");

        // ---- Sweep #3 — even the store's internal Count should reflect exactly one entry.
        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAzure_When403_NoHandleMinted_AndExceptionMessageNotEchoed()
    {
        var sink = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(sink).SetMinimumLevel(LogLevel.Trace));

        var clock = new ControllableClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) },
            clock);

        var adapter = new SentinelAdapter
        {
            CredentialFailure = _ => new global::Azure.RequestFailedException(
                status: (int)HttpStatusCode.Forbidden,
                message: "MUST_NOT_APPEAR_IN_RESPONSE_OR_LOG"),
        };
        var discovery = new AzureAksDiscovery(adapter, store, loggerFactory.CreateLogger<AzureAksDiscovery>());

        var result = await discovery.ListAsync(
            new AzureDiscoveryRequest("sub", null, false, 100, null, IncludeKubeconfig: true),
            CancellationToken.None);

        result.Items.Single().Handoff.Should().BeNull();
        store.Count.Should().Be(0);

        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("MUST_NOT_APPEAR_IN_RESPONSE_OR_LOG");

        var logs = sink.RenderAll();
        // We DO log the cluster name + status (intentional audit) but not the raw provider message.
        logs.Should().NotContain("MUST_NOT_APPEAR_IN_RESPONSE_OR_LOG");
    }

    private sealed class SentinelAdapter : IAzureManagedClusterCollectionAdapter
    {
        public Func<string, Exception?>? CredentialFailure { get; init; }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AzureAksClusterRow> ListAsync(
            string subscriptionId,
            string? resourceGroup,
            [EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998
        {
            yield return new AzureAksClusterRow(
                ResourceId: "/subscriptions/x/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/aks-prod",
                Name: "aks-prod",
                Location: "eastus",
                AgentPoolCount: 3,
                Fqdn: "aks-prod.hcp.eastus.azmk8s.io",
                KubernetesVersion: "1.30.0",
                NodeResourceGroup: "MC_rg_aks-prod_eastus",
                IsPrivateCluster: false);
        }

        public Task<byte[]> GetClusterUserKubeconfigAsync(string resourceId, CancellationToken cancellationToken)
        {
            var failure = CredentialFailure?.Invoke(resourceId);
            if (failure is not null) throw failure;
            // Return a fresh copy — the production code path clears the buffer it stores.
            return Task.FromResult((byte[])KubeconfigPayload.Clone());
        }
    }

    private sealed class ControllableClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public string RenderAll() => string.Join("\n", _lines);

        public void Dispose() { }

        private sealed class Capturing : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;
            public Capturing(CapturingLoggerProvider owner, string category) { _owner = owner; _category = category; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _owner._lines.Enqueue($"{_category} {logLevel}: {formatter(state, exception)} {exception}");
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
