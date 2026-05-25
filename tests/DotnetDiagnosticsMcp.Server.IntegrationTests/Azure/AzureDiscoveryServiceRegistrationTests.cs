using System.Collections.Generic;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

public sealed class AzureDiscoveryServiceRegistrationTests
{
    [Fact]
    public void AddAzureDiscoveryServices_RegistersArmClientFactory_AsSingleton_WhenEnabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDiscovery:Enabled"] = "true",
            })
            .Build();

        var registered = services.AddAzureDiscoveryServices(config);
        registered.Should().BeTrue();

        using var provider = services.BuildServiceProvider();
        var factoryA = provider.GetRequiredService<IAzureArmClientFactory>();
        var factoryB = provider.GetRequiredService<IAzureArmClientFactory>();

        factoryA.Should().NotBeNull();
        factoryB.Should().BeSameAs(factoryA);

        // #232 — backend discovery seams are also registered (default implementations
        // throw NotImplementedException; real backends land in #233 / #234).
        provider.GetRequiredService<DotnetDiagnosticsMcp.Server.Azure.Discovery.IAzureWebAppsDiscovery>()
            .Should().NotBeNull();
        provider.GetRequiredService<DotnetDiagnosticsMcp.Server.Azure.Discovery.IAzureContainerAppsDiscovery>()
            .Should().NotBeNull();
        provider.GetRequiredService<DotnetDiagnosticsMcp.Server.Azure.Discovery.IAzureAksDiscovery>()
            .Should().NotBeNull();
    }

    [Fact]
    public void AddAzureDiscoveryServices_DoesNotRegister_WhenDisabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDiscovery:Enabled"] = "false",
            })
            .Build();

        var registered = services.AddAzureDiscoveryServices(config);
        registered.Should().BeFalse();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IAzureArmClientFactory>().Should().BeNull();
    }

    [Fact]
    public void AddAzureDiscoveryServices_DoesNotRegister_WhenSectionMissing()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        var registered = services.AddAzureDiscoveryServices(config);
        registered.Should().BeFalse();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IAzureArmClientFactory>().Should().BeNull();
    }
}
