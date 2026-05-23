using DotnetDiagnosticsMcp.Core.Security;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class EventSourceAllowlistTests
{
    [Fact]
    public void Defaults_ContainCuratedProviders()
    {
        var allowlist = new EventSourceAllowlist(null);

        allowlist.IsAllowed("System.Net.Http").Should().BeTrue();
        allowlist.IsAllowed("Microsoft.AspNetCore.Hosting").Should().BeTrue();
        allowlist.IsAllowed("System.Runtime").Should().BeTrue();
    }

    [Fact]
    public void UnknownProvider_IsRejected()
    {
        var allowlist = new EventSourceAllowlist(null);
        allowlist.IsAllowed("My.Custom.Source").Should().BeFalse();
    }

    [Fact]
    public void ExtraProviders_FromOptions_AreAccepted()
    {
        var allowlist = new EventSourceAllowlist(new SecurityOptions
        {
            EventSourceAllowlist = { "My.Custom.Source" },
        });

        allowlist.IsAllowed("my.custom.source").Should().BeTrue("comparison should be case-insensitive");
    }
}
