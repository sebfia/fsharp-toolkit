namespace Mercator.HealthChecks

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration

/// Hosted service that runs a lightweight HTTP server for health check endpoints
/// Exposes /health (readiness) and /alive (liveness) endpoints
type HealthCheckServer(serviceName: string, healthCheckService: HealthCheckService, logger: ILogger<HealthCheckServer>, configuration: IConfiguration, port: int) =
    let mutable webApp: WebApplication option = None

    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) =
            task {
                try
                    logger.LogInformation("[{ServiceName}] 🏥 Starting health check server on port {Port}...", serviceName, port)

                    let builder = WebApplication.CreateBuilder()

                    // Allow configuration override via HEALTHCHECK_URLS environment variable
                    // Falls back to a secure localhost binding for dev, but binds to all interfaces in containers/Kubernetes
                    let urls =
                        configuration.["HEALTHCHECK_URLS"]
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            // Kubernetes liveness/readiness probes hit the Pod IP, so we must not bind to localhost there.
                            // Keep localhost for dev by default to avoid exposing the endpoint unintentionally.
                            let isRunningInContainer =
                                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")
                                |> Option.ofObj
                                |> Option.exists (fun v -> v.Equals("true", StringComparison.OrdinalIgnoreCase))

                            let isRunningInKubernetes =
                                Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")
                                |> Option.ofObj
                                |> Option.isSome

                            if isRunningInContainer || isRunningInKubernetes then
                                $"http://0.0.0.0:{port}"
                            else
                                $"http://localhost:{port}")

                    logger.LogInformation("[{ServiceName}] 🔧 Health check server URLs: {Urls}", serviceName, urls)
                    builder.WebHost.UseUrls(urls) |> ignore

                    // Suppress default logging for health check server
                    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore

                    // Add health check service
                    builder.Services.AddSingleton<HealthCheckService>(healthCheckService) |> ignore

                    let app = builder.Build()

                    // Map readiness endpoint - checks if service is ready to handle requests
                    app.MapHealthChecks("/health") |> ignore

                    // Map liveness endpoint - checks if service is alive (only "live" tagged checks)
                    app.MapHealthChecks("/alive", HealthCheckOptions(
                        Predicate = fun r -> r.Tags.Contains("live")
                    )) |> ignore

                    webApp <- Some app

                    // Start web app in background
                    let _ = app.RunAsync(cancellationToken)

                    logger.LogInformation("[{ServiceName}] ✅ Health check endpoints ready:", serviceName)
                    logger.LogInformation("[{ServiceName}]    Liveness:  {Urls}/alive", serviceName, urls)
                    logger.LogInformation("[{ServiceName}]    Readiness: {Urls}/health", serviceName, urls)
                with ex ->
                    logger.LogError(ex, "[{ServiceName}] ❌ Failed to start health check server", serviceName)
                    raise ex
            }

        member _.StopAsync(cancellationToken: CancellationToken) =
            task {
                match webApp with
                | Some app ->
                    logger.LogInformation("[{ServiceName}] 🛑 Stopping health check server...", serviceName)
                    do! app.StopAsync(cancellationToken)
                | None -> ()
            }

/// Extension methods for easily adding health check server to host builder
[<AutoOpen>]
module HealthCheckServerExtensions =
    open Microsoft.Extensions.DependencyInjection

    type IServiceCollection with
        /// Add health check server on specified port (default: 8080)
        /// URLs can be configured via HEALTHCHECK_URLS environment variable
        /// Example: HEALTHCHECK_URLS="http://*:8080" to listen on all interfaces
        /// Default: http://localhost:8080 for better security
        member this.AddHealthCheckServer(serviceName: string, ?port: int) =
            let serverPort = defaultArg port 8080

            this.AddHostedService<HealthCheckServer>(fun sp ->
                let healthCheckService = sp.GetRequiredService<HealthCheckService>()
                let logger = sp.GetRequiredService<ILogger<HealthCheckServer>>()
                let configuration = sp.GetRequiredService<IConfiguration>()
                HealthCheckServer(serviceName, healthCheckService, logger, configuration, serverPort)
            ) |> ignore

            this
