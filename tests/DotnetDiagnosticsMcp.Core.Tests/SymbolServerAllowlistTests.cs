using DotnetDiagnosticsMcp.Core.Security;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class SymbolServerAllowlistTests
{
    [Fact]
    public void LocalPath_IsAlwaysAllowed()
    {
        var allowlist = new SymbolServerAllowlist(null);
        allowlist.Validate("/srv/symbols").IsAllowed.Should().BeTrue();
        allowlist.Validate(@"C:\symbols").IsAllowed.Should().BeTrue();
        allowlist.Validate("cache*/tmp/sym").IsAllowed.Should().BeTrue();
        allowlist.Validate(null).IsAllowed.Should().BeTrue();
        allowlist.Validate("").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void RemoteHost_NotAllowlisted_IsRejected()
    {
        var allowlist = new SymbolServerAllowlist(null);
        var result = allowlist.Validate("srv*c:\\sym*https://msdl.microsoft.com/download/symbols");
        result.IsAllowed.Should().BeFalse();
        result.DeniedHost.Should().Be("msdl.microsoft.com");
    }

    [Fact]
    public void RemoteHost_OnAllowlist_IsAccepted()
    {
        var allowlist = new SymbolServerAllowlist(new SecurityOptions
        {
            SymbolServerAllowlist = { "msdl.microsoft.com" },
        });

        allowlist.Validate("srv*c:\\sym*https://msdl.microsoft.com/download/symbols").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void MixedSegments_RejectsFirstDeniedHost()
    {
        var allowlist = new SymbolServerAllowlist(new SecurityOptions
        {
            SymbolServerAllowlist = { "msdl.microsoft.com" },
        });

        var result = allowlist.Validate("cache*/tmp/sym;srv*https://msdl.microsoft.com/syms;srv*https://attacker.example/syms");
        result.IsAllowed.Should().BeFalse();
        result.DeniedHost.Should().Be("attacker.example");
    }
}
