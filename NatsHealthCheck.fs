namespace Mercator.HealthChecks

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open NATS.Client.Core

/// Health check for NATS connection
/// This checks if NATS is actually connected and responsive
/// Tagged with "ready" - only affects readiness, not liveness
type NatsHealthCheck(serviceName: string, natsClient: INatsClient, logger: ILogger<NatsHealthCheck>) =
    interface IHealthCheck with
        member _.CheckHealthAsync(context, cancellationToken) =
            task {
                try
                    logger.LogDebug("[{ServiceName}] Checking NATS connection health...", serviceName)

                    // Check if client exists, then verify connection type
                    match Option.ofObj natsClient with
                    | None ->
                        logger.LogWarning("[{ServiceName}] NATS client is null", serviceName)
                        return HealthCheckResult.Unhealthy($"[{serviceName}] NATS client is not configured")

                    | Some (:? NatsConnection) ->
                        // NatsConnection type - direct connection established
                        logger.LogDebug("[{ServiceName}] NATS connection appears healthy", serviceName)
                        return HealthCheckResult.Healthy($"[{serviceName}] NATS connection is established")

                    | Some _ ->
                        // Other INatsClient implementation (e.g., NatsClient wrapper)
                        logger.LogDebug("[{ServiceName}] NATS client is configured", serviceName)
                        return HealthCheckResult.Healthy($"[{serviceName}] NATS client is configured")

                with ex ->
                    logger.LogError(ex, "[{ServiceName}] NATS health check failed with exception", serviceName)
                    return HealthCheckResult.Unhealthy($"[{serviceName}] NATS health check failed", ex)
            }

/// Extension methods for adding NATS health check
[<AutoOpen>]
module NatsHealthCheckExtensions =
    open Microsoft.Extensions.DependencyInjection

    type IHealthChecksBuilder with
        /// Add NATS health check (tagged as "ready" - only affects readiness probe)
        member this.AddNatsHealthCheck(serviceName: string) =
            this.Services.AddSingleton<NatsHealthCheck>(fun sp ->
                let natsClient = sp.GetRequiredService<INatsClient>()
                let logger = sp.GetRequiredService<ILogger<NatsHealthCheck>>()
                NatsHealthCheck(serviceName, natsClient, logger)
            ) |> ignore
            this.AddCheck<NatsHealthCheck>(
                $"{serviceName}-nats",
                tags = [| "ready" |],  // Only affects /health, not /alive
                failureStatus = HealthStatus.Unhealthy
            )
