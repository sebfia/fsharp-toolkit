# Changelog

## 2026-09-28

Synced with the Mercator monorepo's toolkit (`mercator-kubernetes/src/toolkit`);
every file here is now identical to it.

### Health probes on Kestrel (Mercator ADR-0024)

`HealthCheckServer.fs` (a hand-written `HttpListener` loop) is replaced by
`HealthProbes.fs`, which serves the probes from a probe-only Kestrel app
(`WebApplication.CreateEmptyBuilder` + `UseKestrelCore`) beside the service's
generic host. The managed `HttpListener` crashed on Linux when a probe arrived
while it started (19 of 19 container starts under early probing; Kestrel 25 of 25).

**Breaking changes:**

- `services.AddHealthCheckServer(name, ?port)` → `services.AddHealthProbes(name, ?port)`
  (open `Mercator.HealthChecks.HealthProbeExtensions`).
- `HealthCheckUrls.normalize` and `HealthCheckResponse.errorJson` are gone. Kestrel
  accepts any address spelling (`+`, `*`, `0.0.0.0`); check exceptions already
  surface as `Unhealthy` entries.
- A project compiling `HealthProbes.fs` needs
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. Under trimming this
  costs ~1.5 MB; keep the empty builder (the slim/default builders' Kestrel config
  loader breaks trimmed apps).

**Unchanged:** port 8080, `/alive` (checks tagged `live`) and `/health`, the per-probe
`Degraded` mapping (503 readiness / 200 liveness), the JSON body, `HEALTHCHECK_URLS`.

**New:** `HealthProbes.map` puts the same endpoints on an existing
`IEndpointRouteBuilder`. Framework logs from the probe app pass only at Warning and
above, so a probe no longer writes two Information lines.

### NatsHealthCheck: real connection state

Reads the NATS connection state instead of checking that a client object exists:
`Open` → Healthy, `Connecting`/`Reconnecting` → Degraded (fails readiness, not
liveness), `Closed`/`Failed` → Unhealthy.
The state is read through `INatsClient.Connection` (`INatsConnection`), so any
client implementation is checked; a client without a connection is Unhealthy.

### ServiceHealthCheck

Unused parameters are `_`-prefixed, so consumers building with warnings as errors
(FS1182) compile it cleanly.

---

## 2026-01-18

### Logging - Production Log Level Set to INFO

Configured global log level based on environment:

- **Production**: INFO minimum (debug logs suppressed)
- **Development**: DEBUG minimum (all logs visible)

Debug logging is preserved in health check implementations for troubleshooting when needed.

**Files changed (in IdentityService):**

- `Program.fs` - Set `builder.Logging.SetMinimumLevel()` and NLog rules based on environment

---

### Health Checks - Added Service Name Parameter

All health check components now require a `serviceName` parameter to identify which service the logs belong to.

**Files changed:**

- `ServiceHealthCheck.fs`
- `NatsHealthCheck.fs`
- `HealthCheckServer.fs`

**Breaking changes to APIs:**

- `ServiceHealthCheck(serviceName, healthStore, logger)` - added `serviceName` as first parameter
- `LivenessHealthCheck(serviceName, healthStore, logger)` - added `serviceName` as first parameter
- `NatsHealthCheck(serviceName, natsClient, logger)` - added `serviceName` as first parameter
- `HealthCheckServer(serviceName, healthCheckService, logger, configuration, port)` - added `serviceName` as first parameter

**Extension method changes:**

```fsharp
// Before
.AddServiceHealthCheck()
.AddLivenessHealthCheck()
.AddNatsHealthCheck()
.AddHealthCheckServer(?port)

// After
.AddServiceHealthCheck(serviceName)
.AddLivenessHealthCheck(serviceName)
.AddNatsHealthCheck(serviceName)
.AddHealthCheckServer(serviceName, ?port)
```

**Log output changes:**

All log messages now include the service name prefix:

- `[{ServiceName}] Executing service health check...`
- `[{ServiceName}] Service is healthy - no errors recorded`
- `[{ServiceName}] NATS connection appears healthy`
- `[{ServiceName}] Starting health check server on port {Port}...`

Health check names are also prefixed with service name:

- `{serviceName}-service` (readiness)
- `{serviceName}-liveness` (liveness)
- `{serviceName}-nats` (NATS connectivity)
