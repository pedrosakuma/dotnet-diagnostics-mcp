# Client setup

`dotnet-diagnostics-mcp` supports **two transports**:

- **`--stdio`** (recommended for local dev): the MCP client (Copilot CLI, Claude
  Desktop, Cursor, ...) spawns the server as a child process per session. No daemon,
  no bearer token, no ports — every `dotnet tool update` + client reload picks up
  the fresh binary automatically (see #74 for the motivation).
- **Streamable HTTP** at `POST /mcp` with `Authorization: Bearer <token-or-jwt>`
  (default; intended for Kubernetes sidecar / shared-server scenarios where one
  long-running server is consumed by multiple clients or pods).

This doc covers the three most common ways to connect.

## 1. Run the server

### Option A — `--stdio` (local dev)

You don't run the server yourself. Point the MCP client at the binary and it will
spawn / tear down the process per session:

```jsonc
// ~/.copilot/mcp-config.json (Copilot CLI)
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "stdio",
      "command": "dotnet-diagnostics-mcp",
      "args": ["--stdio"],
      "tools": ["*"]
    }
  }
}
```

```jsonc
// Claude Desktop / Cursor (claude_desktop_config.json shape)
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "command": "dotnet-diagnostics-mcp",
      "args": ["--stdio"]
    }
  }
}
```

### Option B — Streamable HTTP daemon (sidecar / shared deploy)

```bash
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/DotnetDiagnosticsMcp.Server
# Server listens on http://localhost:5000 (or whatever ASP.NET picks)
```

Sanity check:

```bash
curl -fsS http://localhost:5000/health
# {"status":"ok"}

curl -fsS http://localhost:5000/mcp -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## OIDC quickstart (HTTP transport)

Set these env vars on the server to enable JWT validation next to the existing opaque bearer tokens:

```bash
export MCP_OIDC_ISSUER="https://issuer.example.com"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Optional: require extra issuer-specific claims.
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"diag-client"}'
```

Then send the access token exactly like the legacy bearer path:

```bash
curl -i http://localhost:5000/mcp \
  -H "Authorization: Bearer $ACCESS_TOKEN"
```

### Microsoft Entra ID

```bash
export MCP_OIDC_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
export MCP_OIDC_AUDIENCE="api://dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"<client-id>"}'
```

Use the app registration / managed identity to mint an access token for `api://dotnet-diagnostics-mcp`, and put your MCP scopes in the token's `scp` claim.

### AWS IAM Identity Center

```bash
export MCP_OIDC_ISSUER="https://oidc.<region>.amazonaws.com/id/<provider-id>"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"client_id":"<application-arn-or-client-id>"}'
```

Map your permission set or trusted token issuer so the resulting JWT carries the MCP scopes in `scope` or `scp`.

### Keycloak

```bash
export MCP_OIDC_ISSUER="https://keycloak.example.com/realms/<realm>"
export MCP_OIDC_AUDIENCE="dotnet-diagnostics-mcp"
# Optional when Keycloak stores scopes in a custom claim instead of `scope` / `scp`.
export MCP_OIDC_SCOPE_CLAIM="scope"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"dotnet-diagnostics-mcp-client"}'
```

Create a confidential client or service account, add the MCP scopes to its client scope mapping, and pass the resulting access token in the `Authorization` header.

## 2. Connect from the C# MCP SDK

The pattern used by our integration tests:

```csharp
using ModelContextProtocol.Client;

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5000/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {token}",
    },
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync();
foreach (var tool in tools)
{
    Console.WriteLine($"{tool.Name}: {tool.Description}");
}

var processes = await client.CallToolAsync(
    "inspect_process",
    arguments: new Dictionary<string, object?> { ["view"] = "list" });
```

See [`tests/DotnetDiagnosticsMcp.Server.IntegrationTests/McpToolsTests.cs`](../tests/DotnetDiagnosticsMcp.Server.IntegrationTests/McpToolsTests.cs)
for a full working example covering every tool.

## 3. Connect from Claude Desktop / a generic MCP client

Claude Desktop and other GUI clients typically read a JSON config block. The
exact field names depend on the host, but the shape for a Streamable HTTP
transport with bearer auth is:

```json
{
  "mcpServers": {
    "dotnet-dbg": {
      "transport": "streamable-http",
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

For sidecar deployments, replace `localhost:5000` with the `kubectl port-forward`
target:

```bash
kubectl -n diagnosticsmcp-demo port-forward svc/sample-api-diagnosticsmcp 8787:8787
# then point the client at http://localhost:8787/mcp
```

## 4. Quick smoke test with `curl`

The MCP protocol is JSON-RPC over HTTP; the cheapest way to confirm the server
is reachable and the token is correct is to send an `initialize` request and
follow up with `tools/list`. This is tedious by hand — prefer one of the SDK
or GUI options above. Use `curl` only to verify network reachability and the
401 vs 200 boundary:

```bash
# 401 — wrong token, auth working
curl -i http://localhost:5000/mcp -H "Authorization: Bearer wrong"

# 200 (or 4xx from MCP, not from auth) — token accepted
curl -i http://localhost:5000/mcp -H "Authorization: Bearer $MCP_BEARER_TOKEN"
```

## Operational tips

- **Rotate the token** by changing `MCP_BEARER_TOKEN` (or the Kubernetes
  Secret) and restarting the server.
- **Set a fixed token** in production. The auto-generated ephemeral token is
  convenient for local dev but rotates on every restart.
- **TLS termination** is not built in. Run behind a reverse proxy (nginx,
  Envoy) or a service mesh for TLS and additional access controls.
- **Logs** are JSON-friendly via `SimpleConsole`; pipe stdout into your
  collector of choice.

## Long-running collectors: cutover to MCP-native progress and cancellation

Stage A of [RFC 0002 §7.3 #7 / issue #211](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/211)
adds MCP-native progress and cancellation to `collect_cpu_sample` and
`collect_events`. Clients should stop using the legacy polling bridge as soon
as their MCP runtime supports `notifications/progress` + `notifications/cancelled`
on `tools/call`:

- **C# MCP SDK** (≥ `1.3.0`): pass an `IProgress<ProgressNotificationValue>` to
  `client.CallToolAsync(name, args, progress, cancellationToken)`. Cancellation
  flows through the `CancellationToken`.
- **TypeScript MCP SDK** (≥ `1.5.0`): set `_meta.progressToken` on the
  `tools/call` request and listen for `notifications/progress`. To cancel,
  abort the in-flight request (the SDK then sends an MCP
  `notifications/cancelled` whose `requestId` matches the original
  `tools/call` — **cancellation is request-scoped, not progress-token-scoped**).
- **Generic clients**: any MCP-spec-compliant client that handles
  `notifications/progress` works — the server emits progress on a ~1s cadence
  and a terminal `100%` on completion. When the server-side cancel handler
  wins the race, the call returns a structured envelope with `cancelled: true`;
  when the client transport closes first, the SDK typically surfaces the
  cancellation as an exception. Both are spec-conformant — render either as
  a "stopped" state.

Cutover plan:

1. Update your MCP client SDK to a version that emits a progress token on
   long-running `tools/call`.
2. Stop passing `runAsJob=true`. The server still accepts it in Stage A but
   logs a once-per-process Warning (`runAsJob=true is deprecated…`) so
   operators can confirm no traffic still depends on it.
3. Stop calling `get_collection_status` / `cancel_collection`. They remain in
   Stage A; both will be removed in **Stage B** once
   [issue #211](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/211)
   completes the client-matrix audit.

If your client cannot be updated, the legacy polling path remains functional
in this release.
