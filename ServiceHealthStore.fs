namespace Mercator.HealthChecks

open System
open System.Collections.Concurrent
open Microsoft.Extensions.Logging

/// Represents the severity of a service error
type ErrorSeverity =
    | Resolvable      // Transient errors (network blip, temporary unavailability) - affects readiness
    | NonResolvable   // Critical errors (configuration issue, auth failure) - affects liveness

/// Represents a service error with context
type ServiceError = {
    Component: string
    Message: string
    Severity: ErrorSeverity
    Timestamp: DateTimeOffset
    Exception: exn option
}

/// Store for tracking service health based on actual operational errors
/// This is a singleton that services update when errors occur
type ServiceHealthStore() =
    let errors = ConcurrentDictionary<string, ServiceError>()
    let mutable lastHealthyTime = DateTimeOffset.UtcNow

    /// Record a resolvable error (e.g., NATS connection failed, will retry)
    member _.RecordResolvableError(componentName: string, message: string, ?ex: exn) =
        let error = {
            Component = componentName
            Message = message
            Severity = Resolvable
            Timestamp = DateTimeOffset.UtcNow
            Exception = ex
        }
        errors.[componentName] <- error

    /// Record a non-resolvable error (e.g., invalid configuration, auth failure)
    member _.RecordNonResolvableError(componentName: string, message: string, ?ex: exn) =
        let error = {
            Component = componentName
            Message = message
            Severity = NonResolvable
            Timestamp = DateTimeOffset.UtcNow
            Exception = ex
        }
        errors.[componentName] <- error

    /// Clear error for a specific component (called on successful operation)
    member _.ClearError(componentName: string) =
        errors.TryRemove(componentName) |> ignore
        lastHealthyTime <- DateTimeOffset.UtcNow

    /// Clear all errors
    member _.ClearAllErrors() =
        errors.Clear()
        lastHealthyTime <- DateTimeOffset.UtcNow

    /// Get all current errors
    member _.GetErrors() =
        errors.Values |> Seq.toList

    /// Check if service has any non-resolvable errors (affects liveness)
    member _.HasNonResolvableErrors() =
        errors.Values
        |> Seq.exists (fun e -> e.Severity = NonResolvable)

    /// Check if service has any resolvable errors (affects readiness)
    member _.HasResolvableErrors() =
        errors.Values
        |> Seq.exists (fun e -> e.Severity = Resolvable)

    /// Check if service is completely healthy
    member _.IsHealthy() =
        errors.IsEmpty

    /// Get time since last healthy state
    member _.TimeSinceHealthy() =
        DateTimeOffset.UtcNow - lastHealthyTime

    /// Get error summary for logging
    member _.GetErrorSummary() =
        if errors.IsEmpty then
            "No errors"
        else
            errors.Values
            |> Seq.map (fun e -> $"{e.Component}: {e.Message} ({e.Severity})")
            |> String.concat "; "

/// Extension methods for ILogger to integrate with health store
[<AutoOpen>]
module ServiceHealthStoreLoggingExtensions =
    type ILogger with
        /// Log an error and record it in the health store as resolvable
        member this.LogResolvableError(healthStore: ServiceHealthStore, componentName: string, message: string, ?ex: exn) =
            match ex with
            | Some e ->
                this.LogError(e, $"[{componentName}] {message}")
                healthStore.RecordResolvableError(componentName, message, e)
            | None ->
                this.LogError($"[{componentName}] {message}")
                healthStore.RecordResolvableError(componentName, message)

        /// Log an error and record it in the health store as non-resolvable
        member this.LogNonResolvableError(healthStore: ServiceHealthStore, componentName: string, message: string, ?ex: exn) =
            match ex with
            | Some e ->
                this.LogCritical(e, $"[{componentName}] {message}")
                healthStore.RecordNonResolvableError(componentName, message, e)
            | None ->
                this.LogCritical($"[{componentName}] {message}")
                healthStore.RecordNonResolvableError(componentName, message)

        /// Log a successful operation and clear errors for the component
        member this.LogHealthy(healthStore: ServiceHealthStore, componentName: string, ?message: string) =
            match message with
            | Some msg -> this.LogDebug($"[{componentName}] {msg}")
            | None -> this.LogDebug($"[{componentName}] Operation successful")
            healthStore.ClearError(componentName)

/// Extension for DI registration
[<AutoOpen>]
module ServiceHealthStoreExtensions =
    open Microsoft.Extensions.DependencyInjection

    type IServiceCollection with
        /// Register ServiceHealthStore as a singleton
        member this.AddServiceHealthStore() =
            this.AddSingleton<ServiceHealthStore>()
