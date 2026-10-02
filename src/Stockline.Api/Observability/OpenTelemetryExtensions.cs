using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Stockline.Api.Observability;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Exports traces over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is configured,
    /// so development and tests run exactly as before without a collector.
    /// The exporter itself reads OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_EXPORTER_OTLP_PROTOCOL.
    /// </summary>
    public static IServiceCollection AddStocklineTracing(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuration["OTEL_SERVICE_NAME"] ?? "stockline-api"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                .AddOtlpExporter());

        return services;
    }
}
