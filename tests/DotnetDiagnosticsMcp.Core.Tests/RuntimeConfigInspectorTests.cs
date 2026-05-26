using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class RuntimeConfigInspectorTests
{
    [Fact]
    public void FilterAllowlistedEnvironmentEntries_DropsSecrets_And_Keeps_RuntimePrefixes()
    {
        var filtered = RuntimeConfigInspector.FilterAllowlistedEnvironmentEntries(
        [
            "SECRET_TOKEN=abc",
            "MY_KEY=xyz",
            "DOTNET_gcServer=1",
            "ASPNETCORE_URLS=http://localhost",
        ]);

        filtered.Should().BeEquivalentTo(
        [
            new EnvVarEntry("ASPNETCORE_URLS", "http://localhost"),
            new EnvVarEntry("DOTNET_gcServer", "1"),
        ], options => options.WithStrictOrdering());
        filtered.Should().OnlyContain(entry => RuntimeConfigInspector.IsAllowlistedEnvironmentVariable(entry.Name));
        filtered.Should().NotContain(entry => entry.Name == "SECRET_TOKEN" || entry.Name == "MY_KEY");
    }

    [Fact]
    public void FilterAllowlistedEnvironmentEntries_HandlesEdgeCases()
    {
        var filtered = RuntimeConfigInspector.FilterAllowlistedEnvironmentEntries(
        [
            "dotnet_gcServer=1",           // lowercase prefix
            "DOTNET=no_underscore",        // prefix without underscore
            "DOTNET_SYSTEM_FOO=bar",       // DOTNET_SYSTEM_ prefix
            "COMPlus_TieredCompilation=1", // COMPlus_ prefix
            "=EMPTY_NAME",                 // missing name
            "NO_EQUALS",                   // missing equals
            "DOTNET_Multi=Val=ue",         // multiple equals in value
            "",                            // empty string
            "   ",                         // whitespace only
            "AWS_SECRET_KEY=supersecret",  // non-allowlisted prefix
        ]);

        filtered.Should().BeEquivalentTo(
        [
            new EnvVarEntry("COMPlus_TieredCompilation", "1"),
            new EnvVarEntry("dotnet_gcServer", "1"),       // case-insensitive matching
            new EnvVarEntry("DOTNET_Multi", "Val=ue"),     // equals in value preserved
            new EnvVarEntry("DOTNET_SYSTEM_FOO", "bar"),
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void IsAllowlistedEnvironmentVariable_CaseInsensitive()
    {
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DOTNET_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("dotnet_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DoTnEt_gcServer").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("ASPNETCORE_URLS").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("aspnetcore_urls").Should().BeTrue();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("SECRET_KEY").Should().BeFalse();
    }

    [Fact]
    public void IsAllowlistedEnvironmentVariable_RejectsMalformed()
    {
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("").Should().BeFalse();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("   ").Should().BeFalse();
        RuntimeConfigInspector.IsAllowlistedEnvironmentVariable("DOTNET").Should().BeFalse(); // no underscore
    }
}
