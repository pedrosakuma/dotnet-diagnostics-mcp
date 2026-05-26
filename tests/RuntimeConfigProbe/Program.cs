using System.Text.Json;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;

if (args.Length != 1 || !int.TryParse(args[0], out var processId) || processId <= 0)
{
    Console.Error.WriteLine("Usage: RuntimeConfigProbe <pid>");
    return 2;
}

try
{
    var inspector = new RuntimeConfigInspector();
    var runtimeConfig = await inspector.InspectAsync(processId, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(runtimeConfig));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
