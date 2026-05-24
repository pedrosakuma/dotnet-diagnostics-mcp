using DotnetDiagnosticsMcp.Server.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Stage A of RFC 0002 §7.3 #7 / issue #211 — every call to a long-running collector
/// with <c>runAsJob=true</c> must emit a once-per-process Warning. The legacy job-table
/// path stays functional in Stage A; only the deprecation telemetry is new.
/// </summary>
public sealed class LegacyRunAsJobDeprecationTests
{
    [Fact]
    public void NotifyRunAsJobUse_FiresExactlyOnceAcrossMultipleCalls()
    {
        var (provider, deprecation) = NewDeprecation();

        for (int i = 0; i < 5; i++)
        {
            deprecation.NotifyRunAsJobUse();
        }

        WarningsFor(provider, LegacyRunAsJobDeprecation.RunAsJobWarning).Should().Be(1,
            "the deprecation banner must fire once per process — never spammed on every runAsJob call");
    }

    [Fact]
    public void NotifyRunAsJobUse_Idempotent_PerInstance()
    {
        // The class scopes the latch to the instance — DI registers it as a singleton so
        // 'per-instance' is 'per-process' in production. Tests use fresh instances per case.
        var (provider1, deprecation1) = NewDeprecation();
        var (provider2, deprecation2) = NewDeprecation();

        deprecation1.NotifyRunAsJobUse();
        deprecation1.NotifyRunAsJobUse();
        deprecation2.NotifyRunAsJobUse();

        WarningsFor(provider1, LegacyRunAsJobDeprecation.RunAsJobWarning).Should().Be(1);
        WarningsFor(provider2, LegacyRunAsJobDeprecation.RunAsJobWarning).Should().Be(1);
    }

    [Fact]
    public void NotifyRunAsJobUse_NoLogger_DoesNotThrow()
    {
        // Belt-and-braces: callers that pass null (or rely on the default ctor) must never crash.
        var deprecation = new LegacyRunAsJobDeprecation();
        Action act = () =>
        {
            deprecation.NotifyRunAsJobUse();
            deprecation.NotifyRunAsJobUse();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void RunAsJobWarning_MessageMentionsStageBRemoval()
    {
        // The wording is part of the public contract: operators grep server logs for it
        // and our docs reference the exact phrase. Pin it down so wording drift is caught.
        LegacyRunAsJobDeprecation.RunAsJobWarning.Should().Contain("runAsJob=true is deprecated");
        LegacyRunAsJobDeprecation.RunAsJobWarning.Should().Contain("notifications/progress");
        LegacyRunAsJobDeprecation.RunAsJobWarning.Should().Contain("notifications/cancelled");
        LegacyRunAsJobDeprecation.RunAsJobWarning.Should().Contain("Stage B");
        LegacyRunAsJobDeprecation.RunAsJobWarning.Should().Contain("issue #211");
    }

    private static (ListLoggerProvider Provider, LegacyRunAsJobDeprecation Service) NewDeprecation()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger<LegacyRunAsJobDeprecation>();
        return (provider, new LegacyRunAsJobDeprecation(logger));
    }

    private static int WarningsFor(ListLoggerProvider provider, string message) =>
        provider.Records.Count(r => r.Level == LogLevel.Warning && r.Message == message);
}
