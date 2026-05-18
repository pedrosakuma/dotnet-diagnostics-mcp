using DotnetDbgMcp.Server.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport();

var app = builder.Build();

var token = BearerTokenOptions.LoadOrGenerate(app.Logger);
app.UseMiddleware<BearerTokenMiddleware>(token);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();

namespace DotnetDbgMcp.Server
{
    public partial class Program;
}
