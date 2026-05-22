using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// Maps the orchestrator's per-handle reverse proxy at <c>{ProxyBasePath}/{handleId}/{**rest}</c>.
/// Validates the handle, resolves the cached <see cref="HttpClient"/> from
/// <see cref="IPortForwardManager"/>, swaps the client-supplied <c>Authorization</c> header
/// for the per-attach Pod-local bearer token, forwards the request to the ephemeral
/// container's diagnostics MCP, and streams the response back. The Pod-local secret
/// never leaves the orchestrator process.
/// </summary>
internal static class InvestigationProxyEndpoints
{
    private static readonly string[] ProxyHttpMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Upgrade", "Host",
        "Authorization", // stripped explicitly — orchestrator injects its own
    };

    public static IEndpointRouteBuilder MapInvestigationProxy(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ServiceProvider.GetRequiredService<OrchestratorOptions>();
        var pattern = options.ProxyBasePath.TrimEnd('/') + "/{handleId}/{**rest}";

        app.MapMethods(pattern, ProxyHttpMethods, HandleAsync);
        return app;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("DotnetDiagnosticsMcp.Server.Hosting.InvestigationProxy");

        var handleId = (string?)context.Request.RouteValues["handleId"];
        var rest = (string?)context.Request.RouteValues["rest"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(handleId))
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Missing investigation handle id.").ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IInvestigationStore>();
        var handle = store.GetById(handleId);
        if (handle is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                $"Investigation handle '{handleId}' is unknown.").ConfigureAwait(false);
            return;
        }
        if (handle.State != InvestigationState.Active)
        {
            await WriteProblemAsync(context, StatusCodes.Status410Gone,
                $"Investigation '{handleId}' is in state {handle.State} and cannot be proxied.").ConfigureAwait(false);
            return;
        }

        var manager = context.RequestServices.GetRequiredService<IPortForwardManager>();
        HttpClient client;
        try
        {
            client = await manager.GetOrCreateClientAsync(handle, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OrchestratorException ex)
        {
            logger.LogWarning(ex, "Port-forward setup failed for {HandleId}.", handleId);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
            return;
        }

        var targetPath = "/" + rest.TrimStart('/');
        var targetUri = new UriBuilder(client.BaseAddress!) { Path = targetPath };
        if (context.Request.QueryString.HasValue) targetUri.Query = context.Request.QueryString.Value!.TrimStart('?');

        using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri.Uri);
        CopyRequestHeaders(context.Request, upstream, handle.PodLocalBearerToken);

        if (HasBody(context.Request))
        {
            upstream.Content = new StreamContent(context.Request.Body);
            foreach (var h in context.Request.Headers)
            {
                if (!h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                upstream.Content.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request failed for {HandleId} → {Path}.", handleId, targetPath);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                $"Pod-local diagnostics MCP did not respond: {ex.Message}").ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response);

            // The pod-local MCP server uses Streamable HTTP semantics: the response to
            // the initial POST is often a long-lived text/event-stream where each
            // SSE event is a discrete chunk that the client must observe immediately
            // to complete its initialize handshake. ASP.NET Core's default response
            // body pipeline buffers writes until a threshold is hit or the request
            // completes; with CopyToAsync that means the first SSE event sits in the
            // buffer until the upstream stream closes — which for keep-alive sessions
            // never happens, and the client trips its 100s HttpClient.Timeout. Disable
            // buffering on the response body feature and stream chunks ourselves with
            // an explicit flush after every read so the SSE event reaches the wire as
            // soon as the upstream produces it.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await StreamWithFlushAsync(response.Content, context.Response.Body, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static async Task StreamWithFlushAsync(HttpContent content, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination, string podToken)
    {
        foreach (var h in source.Headers)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            if (h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            destination.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!);
        }
        destination.Headers.TryAddWithoutValidation("Authorization", $"Bearer {podToken}");
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var h in source.Headers)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            destination.Headers[h.Key] = h.Value.ToArray();
        }
        if (source.Content is not null)
        {
            foreach (var h in source.Content.Headers)
            {
                if (HopByHopHeaders.Contains(h.Key)) continue;
                destination.Headers[h.Key] = h.Value.ToArray();
            }
        }
        // ASP.NET Core writes Transfer-Encoding itself when chunking; drop any upstream copy.
        destination.Headers.Remove("Transfer-Encoding");
    }

    private static bool HasBody(HttpRequest request)
        => !HttpMethods.IsGet(request.Method) &&
           !HttpMethods.IsHead(request.Method) &&
           !HttpMethods.IsDelete(request.Method);

    private static Task WriteProblemAsync(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"detail\":{System.Text.Json.JsonSerializer.Serialize(detail)}}}");
    }
}
