namespace Mercator.HealthChecks

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open NATS.Client.Core

/// Pure classification of a NATS connection state.
///
/// Extracted from the health check so the mapping is unit-testable:
/// `NatsConnection.ConnectionState` has a private setter, and driving a real
/// connection into Connecting/Reconnecting requires a live server (it retries
/// indefinitely against a dead endpoint), so those states cannot be reached
/// from a fast unit test.
[<RequireQualifiedAccess>]
module NatsConnectionHealth =

    /// How a connection state should be treated for readiness purposes.
    type Classification =
        /// Connection is open and usable.
        | Connected
        /// Actively recovering (Connecting/Reconnecting). Not ready, but this
        /// resolves itself — it must NOT be reported as a permanent failure.
        | Recovering
        /// Closed/Failed: not recovering on its own.
        | Broken

    /// Classify a raw NATS connection state.
    let classify (state: NatsConnectionState) : Classification =
        match state with
        | NatsConnectionState.Open -> Connected
        | NatsConnectionState.Connecting
        | NatsConnectionState.Reconnecting -> Recovering
        // Closed / Failed / anything the client adds later.
        | _ -> Broken

    /// Map a classification to a health status.
    ///
    /// `Recovering` maps to Degraded, NOT Unhealthy. This mirrors
    /// `ServiceHealthCheck`'s treatment of *resolvable* errors: both still fail
    /// readiness (`HealthCheckResponse` maps Degraded -> 503 on `/health`), so
    /// the Pod leaves the load balancer either way — but the payload and any
    /// status-based alerting can distinguish "recovering" from "broken".
    /// Collapsing these into Unhealthy would page someone for a reconnect that
    /// resolves itself in seconds.
    let toStatus (classification: Classification) : HealthStatus =
        match classification with
        | Connected -> HealthStatus.Healthy
        | Recovering -> HealthStatus.Degraded
        | Broken -> HealthStatus.Unhealthy

/// Health check for NATS connection
/// This checks if NATS is actually connected and responsive
/// Tagged with "ready" - only affects readiness, not liveness
///
/// This inspects the real `NatsConnectionState` rather than merely asserting
/// that a client object exists. The previous implementation only null-checked
/// the client and type-tested it, so it reported Healthy even while the
/// connection was Closed, Failed, or endlessly Reconnecting — readiness was
/// effectively always green.
type NatsHealthCheck(serviceName: string, natsClient: INatsClient, logger: ILogger<NatsHealthCheck>) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_context, cancellationToken) =
            task {
                // HealthCheckService supplies the request/shutdown token. There
                // is no network await in this state inspection, but honoring an
                // already-cancelled request prevents stale work from starting.
                cancellationToken.ThrowIfCancellationRequested()

                try
                    logger.LogDebug("[{ServiceName}] Checking NATS connection health...", serviceName)

                    match Option.ofObj natsClient with
                    | None ->
                        logger.LogWarning("[{ServiceName}] NATS client is null", serviceName)
                        return HealthCheckResult.Unhealthy($"[{serviceName}] NATS client is not configured")

                    | Some client ->
                        // `INatsClient.Connection` exposes the underlying connection for
                        // both NatsConnection and the NatsClient wrapper.
                        let connection =
                            match client with
                            | :? NatsConnection as c -> Some c
                            | _ -> client.Connection |> Option.ofObj |> Option.bind (fun c ->
                                       match c with
                                       | :? NatsConnection as nc -> Some nc
                                       | _ -> None)

                        match connection with
                        | None ->
                            // Unknown INatsClient implementation (e.g. a test double);
                            // we cannot inspect state, so don't fail readiness on it.
                            logger.LogDebug("[{ServiceName}] NATS client type does not expose connection state", serviceName)
                            return HealthCheckResult.Healthy($"[{serviceName}] NATS client is configured (state not observable)")

                        | Some conn ->
                            let state = conn.ConnectionState

                            match NatsConnectionHealth.classify state with
                            | NatsConnectionHealth.Connected ->
                                let server =
                                    conn.ServerInfo
                                    |> Option.ofObj
                                    |> Option.map (fun si -> si.Name)
                                    |> Option.defaultValue "unknown"
                                logger.LogDebug("[{ServiceName}] NATS connection is open (server: {Server})", serviceName, server)
                                return HealthCheckResult.Healthy($"[{serviceName}] NATS connection is open (server: {server})")

                            | NatsConnectionHealth.Recovering ->
                                // Degraded, not Unhealthy — see NatsConnectionHealth.toStatus.
                                logger.LogWarning("[{ServiceName}] NATS connection is {State} (recovering)", serviceName, state)
                                return HealthCheckResult.Degraded($"[{serviceName}] NATS connection is {state} (recovering)")

                            | NatsConnectionHealth.Broken ->
                                logger.LogError("[{ServiceName}] NATS connection is {State}", serviceName, state)
                                return HealthCheckResult.Unhealthy($"[{serviceName}] NATS connection is {state}")

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
                // failureStatus applies ONLY when the check throws an unhandled
                // exception; it does not override a status the check returns
                // itself. So the Degraded result for Connecting/Reconnecting is
                // preserved, while an unexpected crash is still Unhealthy.
                failureStatus = HealthStatus.Unhealthy
            )
