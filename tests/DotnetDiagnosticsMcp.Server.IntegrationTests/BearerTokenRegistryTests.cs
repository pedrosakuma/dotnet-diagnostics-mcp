using System.Collections.Immutable;
using DotnetDiagnosticsMcp.Server.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>Unit tests for <see cref="BearerTokenRegistry"/>: configuration parsing,
/// duplicate detection, the legacy / scoped coexistence contract, and the H9/B1
/// bind-guard handoff. Env-var sensitive cases serialize via <see cref="EnvSerial"/>
/// to keep parallel runs deterministic.</summary>
[Collection(nameof(EnvSerial))]
public sealed class BearerTokenRegistryTests
{
    private static IConfiguration ConfigFrom(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public void Build_ParsesScopedTokens()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "ops-viewer",
            ["Auth:BearerTokens:0:Token"] = "tok-aaa",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
            ["Auth:BearerTokens:0:Scopes:1"] = "eventpipe",
            ["Auth:BearerTokens:1:Name"] = "ops-admin",
            ["Auth:BearerTokens:1:Token"] = "tok-bbb",
            ["Auth:BearerTokens:1:Scopes:0"] = "root",
        });

        var registry = BearerTokenRegistry.Build(config, NullLogger.Instance, allowEphemeralFallback: true);

        registry.Count.Should().Be(2);
        var viewer = registry.TryResolve("tok-aaa");
        viewer.Should().NotBeNull();
        viewer!.Name.Should().Be("ops-viewer");
        viewer.Scopes.Should().BeEquivalentTo(new[] { "read-counters", "eventpipe" });
        viewer.HasScope("read-counters").Should().BeTrue();
        viewer.HasScope("dump-write").Should().BeFalse();

        var admin = registry.TryResolve("tok-bbb");
        admin.Should().NotBeNull();
        admin!.HasScope("anything").Should().BeTrue("root is a wildcard scope");
    }

    [Fact]
    public void Build_DuplicateName_Throws()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "dup",
            ["Auth:BearerTokens:0:Token"] = "tok-a",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
            ["Auth:BearerTokens:1:Name"] = "dup",
            ["Auth:BearerTokens:1:Token"] = "tok-b",
            ["Auth:BearerTokens:1:Scopes:0"] = "read-counters",
        });

        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate Name 'dup'*");
    }

    [Fact]
    public void Build_DuplicateToken_Throws_WithoutLeakingValue()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "a",
            ["Auth:BearerTokens:0:Token"] = "same-secret-value",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
            ["Auth:BearerTokens:1:Name"] = "b",
            ["Auth:BearerTokens:1:Token"] = "same-secret-value",
            ["Auth:BearerTokens:1:Scopes:0"] = "read-counters",
        });

        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().NotContain("same-secret-value", "token values must never appear in exceptions");
        ex.Message.Should().Contain("'b'");
    }

    [Fact]
    public void Build_MissingToken_Throws()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "no-token",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
        });
        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'no-token'*Token*");
    }

    [Fact]
    public void Build_MissingName_Throws()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Token"] = "tok",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
        });
        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:BearerTokens[0]*Name*");
    }

    [Fact]
    public void Build_EmptyScopes_Throws()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "empty",
            ["Auth:BearerTokens:0:Token"] = "tok",
        });
        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'empty'*at least one scope*");
    }

    [Fact]
    public void Build_EmptyScopeString_Throws()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "blank",
            ["Auth:BearerTokens:0:Token"] = "tok",
            ["Auth:BearerTokens:0:Scopes:0"] = "  ",
        });
        var act = () => BearerTokenRegistry.Build(config, NullLogger.Instance, true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'blank'*empty scope*");
    }

    [Fact]
    public void Build_LegacyOnly_ResolvesToLegacyRootPrincipal()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-tok");
        var registry = BearerTokenRegistry.Build(EmptyConfig(), NullLogger.Instance, allowEphemeralFallback: true);

        var principal = registry.TryResolve("legacy-tok");
        principal.Should().NotBeNull();
        principal!.Name.Should().Be(BearerPrincipal.LegacyRootName);
        principal.Scopes.Should().Equal(new[] { BearerPrincipal.RootScope });
        principal.HasScope("anything-at-all").Should().BeTrue();
    }

    [Fact]
    public void Build_BothLegacyAndScoped_ScopedWinsAndLogsWarningOnce()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-tok");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "scoped",
            ["Auth:BearerTokens:0:Token"] = "scoped-tok",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
        });
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        var registry = BearerTokenRegistry.Build(config, lf.CreateLogger("test"), allowEphemeralFallback: true);

        registry.TryResolve("scoped-tok").Should().NotBeNull();
        registry.TryResolve("legacy-tok").Should().BeNull("legacy must be ignored when scoped is present");

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle(r => r.Message.Contains("Legacy MCP_BEARER_TOKEN ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NoAuthAndNoFallback_Throws_BindGuard()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var act = () => BearerTokenRegistry.Build(EmptyConfig(), NullLogger.Instance, allowEphemeralFallback: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*non-loopback*");
    }

    [Fact]
    public void Build_NoAuth_GeneratesEphemeralFallback()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Warning));

        var registry = BearerTokenRegistry.Build(EmptyConfig(), lf.CreateLogger("test"), allowEphemeralFallback: true);
        registry.Count.Should().Be(1);

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.Should().Contain("Generated ephemeral token");
    }

    [Fact]
    public void TryResolve_IteratesAllEntries_NoShortCircuit_FunctionalSmoke()
    {
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        var config = ConfigFrom(new()
        {
            ["Auth:BearerTokens:0:Name"] = "a", ["Auth:BearerTokens:0:Token"] = "tok-aaaa",
            ["Auth:BearerTokens:0:Scopes:0"] = "read-counters",
            ["Auth:BearerTokens:1:Name"] = "b", ["Auth:BearerTokens:1:Token"] = "tok-bbbb",
            ["Auth:BearerTokens:1:Scopes:0"] = "read-counters",
            ["Auth:BearerTokens:2:Name"] = "c", ["Auth:BearerTokens:2:Token"] = "tok-cccc",
            ["Auth:BearerTokens:2:Scopes:0"] = "read-counters",
        });
        var registry = BearerTokenRegistry.Build(config, NullLogger.Instance, true);

        // 100 mixed hits and misses — purely functional check that the no-short-circuit
        // loop still resolves correctly. No timing assertion.
        for (var i = 0; i < 100; i++)
        {
            registry.TryResolve("tok-aaaa")!.Name.Should().Be("a");
            registry.TryResolve("tok-bbbb")!.Name.Should().Be("b");
            registry.TryResolve("tok-cccc")!.Name.Should().Be("c");
            registry.TryResolve("nope").Should().BeNull();
            registry.TryResolve("").Should().BeNull();
        }
    }

    [Fact]
    public void BearerPrincipal_HasScope_HonoursRootWildcard()
    {
        var p = new BearerPrincipal("root-holder", ImmutableHashSet.Create("root"));
        p.HasScope("read-counters").Should().BeTrue();
        p.HasScope("dump-write").Should().BeTrue();
        p.HasScope("anything-future").Should().BeTrue();
    }

    [Fact]
    public void BearerPrincipal_HasScope_HonoursStarWildcard_PerRFCSpelling()
    {
        // RFC 0001 §2.13 / §6.1 spell the root pseudo-scope as "*". Tokens that
        // operators configure with Scopes: ["*"] must satisfy every HasScope check.
        var p = new BearerPrincipal("rfc-root-holder", ImmutableHashSet.Create("*"));
        p.HasScope("read-counters").Should().BeTrue();
        p.HasScope("dump-write").Should().BeTrue();
    }

    [Fact]
    public void BearerPrincipal_HasScope_OnlyMatchesGrantedNonRoot()
    {
        var p = new BearerPrincipal("viewer", ImmutableHashSet.Create("read-counters"));
        p.HasScope("read-counters").Should().BeTrue();
        p.HasScope("eventpipe").Should().BeFalse();
        p.HasScope("root").Should().BeFalse();
        p.HasScope("*").Should().BeFalse();
    }
}
