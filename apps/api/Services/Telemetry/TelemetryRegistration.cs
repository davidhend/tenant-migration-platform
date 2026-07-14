using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MigrationPlatform.Api.Services.Telemetry;

/// <summary>
/// Wires OpenTelemetry tracing, metrics and logging — but ONLY when an exporter
/// is configured. When neither <c>OpenTelemetry:OtlpEndpoint</c> nor an
/// Application Insights connection string is present, the entire OpenTelemetry
/// pipeline is skipped so local/dev runs incur no overhead and cannot fail on
/// missing collectors. Both exporters may be enabled simultaneously.
/// </summary>
public static class TelemetryRegistration
{
    public static string AddPlatformTelemetry(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        var appInsightsConn =
            builder.Configuration["ApplicationInsights:ConnectionString"]
            ?? Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

        var otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);
        var aiEnabled = !string.IsNullOrWhiteSpace(appInsightsConn);

        if (!otlpEnabled && !aiEnabled)
            return "OpenTelemetry disabled (no OTLP endpoint or Application Insights connection string configured).";

        // PlatformMetrics is a singleton regardless, so custom instruments exist;
        // only the export pipeline is gated.
        var serviceName = "MigrationPlatform.Api";
        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation();
                if (otlpEnabled)
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                if (aiEnabled)
                    tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConn);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(PlatformMetrics.MeterName);
                if (otlpEnabled)
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                if (aiEnabled)
                    metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConn);
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (otlpEnabled)
                logging.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
            if (aiEnabled)
                logging.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsightsConn);
        });

        var exporters = string.Join(" + ",
            new[] { otlpEnabled ? "OTLP" : null, aiEnabled ? "AzureMonitor" : null }
                .Where(x => x is not null));
        return $"OpenTelemetry enabled (tracing+metrics+logs) exporting via {exporters}.";
    }
}
