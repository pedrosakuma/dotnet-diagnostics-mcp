using Microsoft.AspNetCore.Mvc.Testing;

namespace DotnetDbgMcp.Server.IntegrationTests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<DotnetDbgMcp.Server.Program>>
{
    private readonly WebApplicationFactory<DotnetDbgMcp.Server.Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<DotnetDbgMcp.Server.Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_ReturnsOk_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Mcp_Returns401_WithoutBearer()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/mcp");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
