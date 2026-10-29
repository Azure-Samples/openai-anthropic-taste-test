using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TasteTest.Services;

public static class TasteTestTelemetry
{
    public const string SourceName = "TasteTest.Chat";

    public static IServiceCollection AddTasteTestTelemetry(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        bool console = configuration.GetValue<bool>("TASTE_TEST_TELEMETRY_CONSOLE");
        string? configuredEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        Uri? endpoint = null;
        if (!string.IsNullOrEmpty(configuredEndpoint) &&
            (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP or HTTPS collector URL.");
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("TasteTest"))
            .WithTracing(tracing =>
            {
                tracing.AddSource(SourceName);
                if (console)
                {
                    tracing.AddConsoleExporter();
                }

                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = SignalEndpoint(endpoint, "traces");
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(SourceName);
                if (console)
                {
                    metrics.AddConsoleExporter();
                }

                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = SignalEndpoint(endpoint, "metrics");
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });
        return services;
    }

    private static Uri SignalEndpoint(Uri endpoint, string signal) =>
        new UriBuilder(endpoint) { Path = $"{endpoint.AbsolutePath.TrimEnd('/')}/v1/{signal}" }.Uri;
}
