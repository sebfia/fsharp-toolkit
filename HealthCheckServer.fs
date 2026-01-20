namespace Mercator.HealthChecks

open System
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration

/// Hosted service that runs a lightweight HTTP server for health check endpoints using HttpListener
/// Exposes /health (readiness) and /alive (liveness) endpoints
type HealthCheckServer(serviceName: string, healthCheckService: HealthCheckService, logger: ILogger<HealthCheckServer>, configuration: IConfiguration, port: int) =
    let mutable httpListener: HttpListener option = None
    let mutable listenerTask: Task option = None
    let mutable cts: CancellationTokenSource option = None

    let respondWithJson (context: HttpListenerContext) (statusCode: int) (json: string) =
        task {
            try
                let response = context.Response
                response.StatusCode <- statusCode
                response.ContentType <- "application/json"
                let buffer = Encoding.UTF8.GetBytes(json)
                response.ContentLength64 <- int64 buffer.Length
                do! response.OutputStream.WriteAsync(buffer, 0, buffer.Length)
                response.Close()
            with ex ->
                logger.LogError(ex, "[{ServiceName}] Error sending response", serviceName)
        }

    let handleHealthCheck (context: HttpListenerContext) (predicate: Func<HealthCheckRegistration, bool> option) =
        task {
            try
                let! result =
                    match predicate with
                    | Some p -> healthCheckService.CheckHealthAsync(p, CancellationToken.None)
                    | None -> healthCheckService.CheckHealthAsync(CancellationToken.None)

                let statusCode =
                    match result.Status with
                    | HealthStatus.Healthy -> 200
                    | HealthStatus.Degraded -> 200
                    | HealthStatus.Unhealthy -> 503
                    | _ -> 503

                let json =
                    let entries =
                        result.Entries
                        |> Seq.map (fun kvp ->
                            $"\"{kvp.Key}\": {{\"status\": \"{kvp.Value.Status}\", \"description\": \"{kvp.Value.Description}\"}}")
                        |> String.concat ", "
                    $"{{\"status\": \"{result.Status}\", \"entries\": {{{entries}}}}}"

                do! respondWithJson context statusCode json
            with ex ->
                logger.LogError(ex, "[{ServiceName}] Health check failed", serviceName)
                do! respondWithJson context 503 "{\"status\": \"Unhealthy\", \"error\": \"Health check exception\"}"
        }

    let processRequest (context: HttpListenerContext) =
        task {
            let path = context.Request.Url.AbsolutePath.ToLower()

            match path with
            | "/health" ->
                do! handleHealthCheck context None
            | "/alive" ->
                // Liveness check - only checks tagged with "live"
                let livePredicate = Func<HealthCheckRegistration, bool>(fun r -> r.Tags.Contains("live"))
                do! handleHealthCheck context (Some livePredicate)
            | _ ->
                do! respondWithJson context 404 "{\"error\": \"Not found\"}"
        }

    let listenerLoop (listener: HttpListener) (cancellationToken: CancellationToken) =
        task {
            try
                while not cancellationToken.IsCancellationRequested do
                    let! context = listener.GetContextAsync()
                    // Process request in background
                    Task.Run(fun () -> processRequest context, cancellationToken) |> ignore
            with
            | :? HttpListenerException as ex when ex.ErrorCode = 995 || ex.ErrorCode = 500 ->
                // Listener was stopped (995) or server unavailable (500) - this is expected during shutdown
                logger.LogDebug("[{ServiceName}] HttpListener stopped", serviceName)
            | :? ObjectDisposedException ->
                // Listener was disposed during shutdown - this is expected
                logger.LogDebug("[{ServiceName}] HttpListener disposed", serviceName)
            | :? OperationCanceledException ->
                logger.LogDebug("[{ServiceName}] HttpListener cancelled", serviceName)
            | ex ->
                logger.LogError(ex, "[{ServiceName}] Error in health check listener loop", serviceName)
        }

    interface IHostedService with
        member _.StartAsync(cancellationToken: CancellationToken) =
            task {
                try
                    logger.LogInformation("[{ServiceName}] 🏥 Starting health check server on port {Port}...", serviceName, port)

                    // Allow configuration override via HEALTHCHECK_URLS environment variable
                    let urls =
                        configuration.["HEALTHCHECK_URLS"]
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            // Kubernetes liveness/readiness probes hit the Pod IP, so we must not bind to localhost there.
                            let isRunningInContainer =
                                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")
                                |> Option.ofObj
                                |> Option.exists (fun v -> v.Equals("true", StringComparison.OrdinalIgnoreCase))

                            let isRunningInKubernetes =
                                Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")
                                |> Option.ofObj
                                |> Option.isSome

                            if isRunningInContainer || isRunningInKubernetes then
                                $"http://+:{port}/"  // Bind to all interfaces
                            else
                                $"http://localhost:{port}/")  // Localhost only for dev

                    logger.LogInformation("[{ServiceName}] 🔧 Health check server URLs: {Urls}", serviceName, urls)

                    let listener = new HttpListener()
                    listener.Prefixes.Add(urls)
                    listener.Start()
                    httpListener <- Some listener

                    let tokenSource = new CancellationTokenSource()
                    cts <- Some tokenSource

                    // Start listening loop in background
                    let task = listenerLoop listener tokenSource.Token
                    listenerTask <- Some task

                    logger.LogInformation("[{ServiceName}] ✅ Health check endpoints ready:", serviceName)
                    logger.LogInformation("[{ServiceName}]    Liveness:  {Urls}alive", serviceName, urls)
                    logger.LogInformation("[{ServiceName}]    Readiness: {Urls}health", serviceName, urls)
                with ex ->
                    logger.LogError(ex, "[{ServiceName}] ❌ Failed to start health check server", serviceName)
                    raise ex
            }

        member _.StopAsync(cancellationToken: CancellationToken) =
            task {
                logger.LogInformation("[{ServiceName}] 🛑 Stopping health check server...", serviceName)

                // Cancel the listening loop first to signal it to stop
                match cts with
                | Some tokenSource ->
                    tokenSource.Cancel()
                | None -> ()

                // Then stop the HttpListener to unblock any pending GetContextAsync
                match httpListener with
                | Some listener ->
                    listener.Stop()
                    listener.Close()
                | None -> ()

                // Wait for listener task to complete (with timeout)
                match listenerTask with
                | Some task ->
                    try
                        let! completed = Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5.0)))
                        if completed <> task then
                            logger.LogWarning("[{ServiceName}] Health check server did not stop gracefully within timeout", serviceName)
                    with ex ->
                        logger.LogDebug(ex, "[{ServiceName}] Exception while waiting for listener task to complete (expected during shutdown)", serviceName)
                | None -> ()

                // Dispose cancellation token source
                match cts with
                | Some tokenSource ->
                    tokenSource.Dispose()
                | None -> ()
            }

/// Extension methods for easily adding health check server to host builder
[<AutoOpen>]
module HealthCheckServerExtensions =
    open Microsoft.Extensions.DependencyInjection

    type IServiceCollection with
        /// Add health check server on specified port (default: 8080)
        /// URLs can be configured via HEALTHCHECK_URLS environment variable
        /// Example: HEALTHCHECK_URLS="http://+:8080/" to listen on all interfaces
        /// Default: http://localhost:8080/ for better security in dev
        member this.AddHealthCheckServer(serviceName: string, ?port: int) =
            let serverPort = defaultArg port 8080

            this.AddHostedService<HealthCheckServer>(fun sp ->
                let healthCheckService = sp.GetRequiredService<HealthCheckService>()
                let logger = sp.GetRequiredService<ILogger<HealthCheckServer>>()
                let configuration = sp.GetRequiredService<IConfiguration>()
                HealthCheckServer(serviceName, healthCheckService, logger, configuration, serverPort)
            ) |> ignore

            this
