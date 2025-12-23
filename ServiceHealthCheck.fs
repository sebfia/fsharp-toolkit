namespace Mercator.HealthChecks

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging

/// Health check based on actual service errors tracked in ServiceHealthStore
/// This provides real operational health status, not just "does the client exist"
type ServiceHealthCheck(healthStore: ServiceHealthStore, logger: ILogger<ServiceHealthCheck>) =

    interface IHealthCheck with
        member _.CheckHealthAsync(context, cancellationToken) =
            task {
                logger.LogDebug("Executing service health check...")

                let errors = healthStore.GetErrors()

                if healthStore.IsHealthy() then
                    logger.LogDebug("Service is healthy - no errors recorded")
                    return HealthCheckResult.Healthy("Service is operating normally")

                elif healthStore.HasNonResolvableErrors() then
                    // Non-resolvable errors mean the service cannot function properly
                    // This should trigger a restart (liveness failure)
                    let nonResolvableErrors =
                        errors
                        |> List.filter (fun e -> e.Severity = NonResolvable)

                    let errorDetails = StringBuilder()
                    errorDetails.AppendLine("Non-resolvable errors detected:") |> ignore

                    for error in nonResolvableErrors do
                        errorDetails.AppendLine($"  - [{error.Component}] {error.Message}") |> ignore

                    let message = errorDetails.ToString()
                    logger.LogError(message)

                    // Create detailed data for debugging
                    let data = readOnlyDict [
                        for error in nonResolvableErrors do
                            error.Component, box error.Message
                    ]

                    return HealthCheckResult.Unhealthy(message, data = data)

                elif healthStore.HasResolvableErrors() then
                    // Resolvable errors mean dependencies are down but service is still alive
                    // This should remove from load balancer (readiness failure) but not restart
                    let resolvableErrors =
                        errors
                        |> List.filter (fun e -> e.Severity = Resolvable)

                    let errorDetails = StringBuilder()
                    errorDetails.AppendLine("Resolvable errors detected (dependencies unavailable):") |> ignore

                    for error in resolvableErrors do
                        let age = DateTimeOffset.UtcNow - error.Timestamp
                        errorDetails.AppendLine($"  - [{error.Component}] {error.Message} (age: {age.TotalSeconds:F0}s)") |> ignore

                    let message = errorDetails.ToString()
                    logger.LogWarning(message)

                    // Create detailed data for debugging
                    let data = readOnlyDict [
                        for error in resolvableErrors do
                            error.Component, box $"{error.Message} (timestamp: {error.Timestamp:o})"
                    ]

                    // Return Degraded status - service is alive but not ready
                    return HealthCheckResult.Degraded(message, data = data)

                else
                    // Should not reach here, but return healthy as fallback
                    return HealthCheckResult.Healthy("Service is operating normally")
            }

/// Liveness-only health check - only checks for non-resolvable errors
/// This should ONLY fail if the service needs to be restarted
type LivenessHealthCheck(healthStore: ServiceHealthStore, logger: ILogger<LivenessHealthCheck>) =

    interface IHealthCheck with
        member _.CheckHealthAsync(context, cancellationToken) =
            task {
                logger.LogDebug("Executing liveness health check...")

                if healthStore.HasNonResolvableErrors() then
                    let errors =
                        healthStore.GetErrors()
                        |> List.filter (fun e -> e.Severity = NonResolvable)

                    let errorSummary =
                        errors
                        |> List.map (fun e -> $"[{e.Component}] {e.Message}")
                        |> String.concat "; "

                    let message = $"Service has non-resolvable errors: {errorSummary}"
                    logger.LogCritical(message)

                    return HealthCheckResult.Unhealthy(message)
                else
                    logger.LogDebug("Service is alive - no non-resolvable errors")
                    return HealthCheckResult.Healthy("Service process is alive")
            }

/// Extension methods for adding health checks
[<AutoOpen>]
module ServiceHealthCheckExtensions =
    open Microsoft.Extensions.DependencyInjection

    type IHealthChecksBuilder with
        /// Add service health check (readiness) - checks all errors
        /// Tagged as "ready" - affects /health endpoint
        member this.AddServiceHealthCheck() =
            this.AddCheck<ServiceHealthCheck>(
                "service",
                tags = [| "ready" |],
                failureStatus = HealthStatus.Degraded  // Degraded, not Unhealthy
            )

        /// Add liveness health check - only checks non-resolvable errors
        /// Tagged as "live" - affects /alive endpoint
        member this.AddLivenessHealthCheck() =
            this.AddCheck<LivenessHealthCheck>(
                "liveness",
                tags = [| "live" |],
                failureStatus = HealthStatus.Unhealthy
            )
