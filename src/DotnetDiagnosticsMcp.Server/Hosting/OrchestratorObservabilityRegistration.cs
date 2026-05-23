using System.IO;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DotnetDiagnosticsMcp.Server.Hosting;

internal static class OrchestratorObservabilityRegistration
{
    public static void AddOrchestratorObservability(this WebApplicationBuilder builder, bool orchestratorEnabled)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddOrchestratorObservabilityCore(
            builder.Services,
            builder.Configuration,
            orchestratorEnabled,
            enablePrometheusExporter: true,
            auditWriter: null);
    }

    public static void AddOrchestratorObservability(this HostApplicationBuilder builder, bool orchestratorEnabled)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddOrchestratorObservabilityCore(
            builder.Services,
            builder.Configuration,
            orchestratorEnabled,
            enablePrometheusExporter: false,
            auditWriter: Console.Error);
    }

    private static void AddOrchestratorObservabilityCore(
        IServiceCollection services,
        ConfigurationManager configuration,
        bool orchestratorEnabled,
        bool enablePrometheusExporter,
        TextWriter? auditWriter)
    {
        var options = new OrchestratorObservabilityOptions();
        configuration.GetSection(OrchestratorObservabilityOptions.SectionName).Bind(options);
        options.MetricsOpen = options.MetricsOpen || IsEnabledEnvironmentFlag("MCP_METRICS_OPEN");

        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => new AuditLogWriter(auditWriter));
        if (orchestratorEnabled)
        {
            services.TryAddSingleton<OrchestratorObservability>();
        }

        var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
        var openTelemetry = services.AddOpenTelemetry();

        openTelemetry.WithMetrics(metrics =>
        {
            metrics
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter("System.Runtime");

            if (orchestratorEnabled)
            {
                metrics.AddMeter(OrchestratorObservability.MeterName);
            }

            if (enablePrometheusExporter && options.MetricsEnabled)
            {
                metrics.AddPrometheusExporter();
            }

            if (hasOtlpEndpoint)
            {
                metrics.AddOtlpExporter();
            }
        });

        openTelemetry.WithTracing(tracing =>
        {
            if (orchestratorEnabled)
            {
                tracing.AddSource(OrchestratorObservability.ActivitySourceName);
            }

            if (hasOtlpEndpoint)
            {
                tracing.AddOtlpExporter();
            }
        });
    }

    public static void MapOrchestratorObservability(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<OrchestratorObservabilityOptions>();
        if (!options.MetricsEnabled)
        {
            return;
        }

        app.UseWhen(
            context => context.Request.Path.Equals("/metrics", StringComparison.Ordinal),
            branch => branch.Use(async (context, next) =>
            {
                if (options.MetricsOpen)
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }

                var principal = context.GetBearerPrincipal();
                if (principal is null)
                {
                    await WriteMetricsProblemAsync(
                        context,
                        StatusCodes.Status401Unauthorized,
                        "unauthenticated",
                        "The /metrics endpoint requires a bearer token with the metrics-read scope.")
                        .ConfigureAwait(false);
                    return;
                }

                if (!principal.HasScope(OrchestratorObservability.MetricsReadScope))
                {
                    await WriteMetricsProblemAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        "forbidden",
                        "The /metrics endpoint requires the metrics-read scope.")
                        .ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
            }));

        app.MapPrometheusScrapingEndpoint("/metrics");
    }

    private static async Task WriteMetricsProblemAsync(HttpContext context, int statusCode, string kind, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":{{\"kind\":\"{kind}\",\"message\":\"{detail}\",\"required_scope\":\"{OrchestratorObservability.MetricsReadScope}\"}}}}")
            .ConfigureAwait(false);
    }

    private static bool IsEnabledEnvironmentFlag(string variableName)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(raw, "1", StringComparison.Ordinal) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class OrchestratorObservabilityOptions
{
    public const string SectionName = "Observability";

    public bool MetricsEnabled { get; set; } = true;

    public bool MetricsOpen { get; set; }
}
