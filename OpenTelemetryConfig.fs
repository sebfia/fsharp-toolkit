namespace Mercator.Observability

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace

[<AutoOpen>]
module OpenTelemetryExtensions =

    type IHostApplicationBuilder with
        /// Configure OpenTelemetry for logging, metrics, and tracing
        /// Automatically exports to OTLP endpoint if OTEL_EXPORTER_OTLP_ENDPOINT is configured
        member this.ConfigureOpenTelemetry() =
            let config = this.Configuration
            let serviceName = this.Environment.ApplicationName

            // Check if OTLP endpoint is configured
            let otlpEndpoint = config.["OTEL_EXPORTER_OTLP_ENDPOINT"]
            let useOtlp = not (String.IsNullOrWhiteSpace(otlpEndpoint))

            // Configure OpenTelemetry with metrics and tracing
            this.Services
                .AddOpenTelemetry()
                .ConfigureResource(fun (resource: ResourceBuilder) ->
                    resource.AddService(serviceName) |> ignore
                )
                .WithMetrics(fun (metrics: MeterProviderBuilder) ->
                    metrics.AddRuntimeInstrumentation() |> ignore
                    if useOtlp then metrics.AddOtlpExporter() |> ignore
                )
                .WithTracing(fun (tracing: TracerProviderBuilder) ->
                    tracing.AddSource(serviceName) |> ignore
                    if useOtlp then tracing.AddOtlpExporter() |> ignore
                )
                |> ignore

            // Add OpenTelemetry logging
            this.Logging.AddOpenTelemetry(fun (logging: OpenTelemetryLoggerOptions) ->
                logging.IncludeFormattedMessage <- true
                logging.IncludeScopes <- true
                if useOtlp then logging.AddOtlpExporter() |> ignore
            ) |> ignore

            this
