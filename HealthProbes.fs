namespace Mercator.HealthChecks

open System
open System.Buffers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Which Kubernetes probe an incoming request represents. The two probes
/// deliberately map `Degraded` differently — see `HealthCheckResponse.statusCode`.
type ProbeKind =
    /// `/health` — readiness. Failing removes the Pod from Service endpoints.
    | Readiness
    /// `/alive` — liveness. Failing restarts the container.
    | Liveness

/// Pure helpers for turning a `HealthReport` into an HTTP response.
[<RequireQualifiedAccess>]
module HealthCheckResponse =

    /// Map a health status to an HTTP status code for the given probe.
    ///
    /// `Degraded` is the interesting case, and the two probes disagree BY DESIGN:
    ///   * Readiness (`/health`): Degraded -> 503. `ServiceHealthCheck` returns
    ///     Degraded for *resolvable* (transient dependency) errors, whose whole
    ///     purpose is "alive but not ready" — the Pod must leave the load
    ///     balancer. Returning 200 here made the Resolvable/NonResolvable
    ///     severity distinction cosmetic.
    ///   * Liveness (`/alive`): Degraded -> 200. A transient dependency outage
    ///     must never restart the container; only NonResolvable errors
    ///     (surfaced as Unhealthy by `LivenessHealthCheck`) may do that.
    let statusCode (probe: ProbeKind) (status: HealthStatus) : int =
        match probe, status with
        | _, HealthStatus.Healthy -> 200
        | Liveness, HealthStatus.Degraded -> 200
        | Readiness, HealthStatus.Degraded -> 503
        | _, HealthStatus.Unhealthy -> 503
        | _, _ -> 503

    /// Serialize a health report to JSON using Utf8JsonWriter.
    ///
    /// Descriptions routinely contain quotes and newlines (`ServiceHealthCheck`
    /// builds multi-line ones), so they must be escaped. Utf8JsonWriter does
    /// that and is trim-safe (no reflection, no serializer metadata).
    let toJson (report: HealthReport) : string =
        let buffer = ArrayBufferWriter<byte>()
        (
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("status", report.Status.ToString())
            writer.WritePropertyName("entries")
            writer.WriteStartObject()

            for kvp in report.Entries do
                writer.WritePropertyName(kvp.Key)
                writer.WriteStartObject()
                writer.WriteString("status", kvp.Value.Status.ToString())
                writer.WriteString("description", kvp.Value.Description)
                writer.WriteEndObject()

            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.Flush()
        )
        Encoding.UTF8.GetString(buffer.WrittenSpan)

/// The Kubernetes probe endpoints (`/alive`, `/health`) on Kestrel — ADR-0024.
///
/// `map` puts them on any endpoint route builder (web-ui maps them onto its
/// own app). Workers have no HTTP app, so `build` makes a dedicated one:
/// `WebApplication.CreateEmptyBuilder` + `UseKestrelCore`, which is only the
/// HTTP/1.1 server and routing — no configuration sources, no HTTPS, no
/// `Kestrel` config section. That last point matters under trimming: the
/// config loader is what enumerates `X509Certificate2Collection`, which the
/// trimmer breaks in an app that never touches certificates.
[<RequireQualifiedAccess>]
module HealthProbes =

    /// Tag that puts a health check on the liveness probe (`/alive`).
    [<Literal>]
    let LiveTag = "live"

    let path (probe: ProbeKind) : string =
        match probe with
        | Readiness -> "/health"
        | Liveness -> "/alive"

    /// Endpoint options for one probe: which checks run, how each status maps
    /// to an HTTP code (`HealthCheckResponse.statusCode`), and the JSON body.
    let options (probe: ProbeKind) : HealthCheckOptions =
        let predicate =
            match probe with
            | Readiness -> fun (_: HealthCheckRegistration) -> true
            | Liveness -> fun (registration: HealthCheckRegistration) -> registration.Tags.Contains LiveTag

        HealthCheckOptions(
            Predicate = predicate,
            ResultStatusCodes =
                ([ HealthStatus.Healthy; HealthStatus.Degraded; HealthStatus.Unhealthy ]
                 |> List.map (fun status -> status, HealthCheckResponse.statusCode probe status)
                 |> dict),
            ResponseWriter =
                (fun (context: HttpContext) (report: HealthReport) ->
                    context.Response.ContentType <- "application/json"
                    context.Response.WriteAsync(HealthCheckResponse.toJson report)))

    /// Map `/health` (readiness) and `/alive` (liveness) onto an endpoint builder.
    let map (endpoints: IEndpointRouteBuilder) : unit =
        [ Readiness; Liveness ]
        |> List.iter (fun probe -> endpoints.MapHealthChecks(path probe, options probe) |> ignore)

    /// Where the probe listener binds. `HEALTHCHECK_URLS` wins. Otherwise all
    /// interfaces inside a container or Pod (the kubelet probes the Pod IP),
    /// and loopback everywhere else. Kestrel accepts `+`, `*` and `0.0.0.0`
    /// alike as the any-address wildcard.
    let bindUrl (configured: string option) (inContainer: bool) (port: int) : string =
        match configured |> Option.map (fun url -> url.Trim()) with
        | Some url when url <> "" -> url
        | _ when inContainer -> $"http://+:{port}"
        | _ -> $"http://localhost:{port}"

    /// True inside a container image (`DOTNET_RUNNING_IN_CONTAINER`, set by the
    /// .NET base images) or a Kubernetes Pod (`KUBERNETES_SERVICE_HOST`).
    let runningInContainer () : bool =
        let variable = Environment.GetEnvironmentVariable >> Option.ofObj
        (variable "DOTNET_RUNNING_IN_CONTAINER"
         |> Option.exists (fun value -> value.Equals("true", StringComparison.OrdinalIgnoreCase)))
        || (variable "KUBERNETES_SERVICE_HOST" |> Option.isSome)

    /// The probe app's host must not own process signals: the service's own
    /// host handles SIGTERM and stops this app through `IHostedService.StopAsync`.
    let private passiveLifetime =
        { new IHostLifetime with
            member _.WaitForStartAsync _ = Task.CompletedTask
            member _.StopAsync _ = Task.CompletedTask }

    /// Hands the probe app's loggers to the service's own logging.
    let private forwardTo (loggerFactory: ILoggerFactory) =
        { new ILoggerProvider with
            member _.CreateLogger(category) = loggerFactory.CreateLogger(category)
            member _.Dispose() = () }

    /// A probe-only Kestrel app on `url`, answering from the service's own
    /// `HealthCheckService` and logging through its `ILoggerFactory`.
    /// ASP.NET Core logs every request at Information, and the kubelet probes
    /// every few seconds, so only framework warnings and errors pass through.
    let build (url: string) (healthChecks: HealthCheckService) (loggerFactory: ILoggerFactory) : WebApplication =
        let builder = WebApplication.CreateEmptyBuilder(WebApplicationOptions())
        builder.WebHost.UseKestrelCore().UseUrls(url) |> ignore
        builder.Logging.AddProvider(forwardTo loggerFactory).AddFilter("Microsoft", LogLevel.Warning) |> ignore
        builder.Services
            .AddRoutingCore()
            .AddSingleton<HealthCheckService>(healthChecks)
            .AddSingleton<IHostLifetime>(passiveLifetime)
        |> ignore
        let app = builder.Build()
        map app
        app

/// Runs the probe app beside a worker's generic host. Starting it binds the
/// port, so a taken port fails host startup instead of leaving a Pod whose
/// probes can never pass.
type HealthProbeServer(serviceName: string, url: string, healthChecks: HealthCheckService, loggerFactory: ILoggerFactory) =
    let logger = loggerFactory.CreateLogger<HealthProbeServer>()
    let app = HealthProbes.build url healthChecks loggerFactory

    interface IHostedService with
        member _.StartAsync(cancellationToken) =
            task {
                do! app.StartAsync(cancellationToken)
                logger.LogInformation(
                    "[{ServiceName}] Health probes on {Urls}: /alive (liveness), /health (readiness)",
                    serviceName,
                    String.Join(", ", app.Urls))
            }

        member _.StopAsync(cancellationToken) = app.StopAsync(cancellationToken)

    interface IAsyncDisposable with
        member _.DisposeAsync() = app.DisposeAsync()

[<AutoOpen>]
module HealthProbeExtensions =

    type IServiceCollection with
        /// Serve `/alive` and `/health` for Kubernetes on `port` (default 8080)
        /// from the checks registered with `AddHealthChecks()`. Set
        /// `HEALTHCHECK_URLS` to override the bind address (see `HealthProbes.bindUrl`).
        member services.AddHealthProbes(serviceName: string, ?port: int) : IServiceCollection =
            services.AddHostedService<HealthProbeServer>(fun provider ->
                let url =
                    HealthProbes.bindUrl
                        (provider.GetRequiredService<IConfiguration>().["HEALTHCHECK_URLS"] |> Option.ofObj)
                        (HealthProbes.runningInContainer ())
                        (defaultArg port 8080)

                new HealthProbeServer(
                    serviceName,
                    url,
                    provider.GetRequiredService<HealthCheckService>(),
                    provider.GetRequiredService<ILoggerFactory>()))
