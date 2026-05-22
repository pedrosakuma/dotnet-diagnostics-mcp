using System.Collections.Generic;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

public sealed class OrchestratorServiceRegistrationTests
{
    [Fact]
    public void AddOrchestratorServices_RegistersSessionBinder_AsSingleton()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "true",
            })
            .Build();

        var registered = services.AddOrchestratorServices(config);
        registered.Should().BeTrue();

        using var provider = services.BuildServiceProvider();
        var binderA = provider.GetRequiredService<IInvestigationSessionBinder>();
        var binderB = provider.GetRequiredService<IInvestigationSessionBinder>();

        binderA.Should().BeOfType<MemoryInvestigationSessionBinder>();
        binderB.Should().BeSameAs(binderA);
    }

    [Fact]
    public void AddOrchestratorServices_DoesNotRegister_WhenDisabled()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "false",
            })
            .Build();

        var registered = services.AddOrchestratorServices(config);
        registered.Should().BeFalse();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IInvestigationSessionBinder>().Should().BeNull();
    }
}
