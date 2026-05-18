namespace DotnetDbgMcp.Core.Tests;

public class CoreInfoTests
{
    [Fact]
    public void SchemaVersionIsSet()
    {
        Assert.False(string.IsNullOrWhiteSpace(CoreInfo.SchemaVersion));
    }
}
