# Changelog

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
