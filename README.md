# F# Toolkit

A collection of reusable F# utility modules for common tasks across projects.

## Modules

### ConfigurationHelpers.fs

Provides helpers for working with .NET configuration, parsing values, and runtime environment detection.

**Features:**
- Configuration value extraction with `tryGetConfigValue`, `tryGetSectionValue`, etc.
- Type-safe parsing functions: `tryParseInt`, `tryParseInt64`, `tryParseBool`, `tryParseFloat`
- Docker/Kubernetes container detection with `isRunningInDocker()`
- Template expansion with environment variables
- OS-aware path manipulation

### Health checks (`ServiceHealthStore.fs`, `ServiceHealthCheck.fs`, `NatsHealthCheck.fs`, `HealthProbes.fs`)

Kubernetes probes on Kestrel: `/alive` (liveness, checks tagged `live`) and `/health`
(readiness) on port 8080. Readiness maps `Degraded` to 503 and liveness maps it to
200. Both return a JSON report.

```fsharp
open Mercator.HealthChecks

services.AddServiceHealthStore() |> ignore
services.AddHealthChecks()
    .AddLivenessHealthCheck("my-service")
    .AddServiceHealthCheck("my-service")
|> ignore
services.AddHealthProbes("my-service") |> ignore   // probe-only listener on :8080
```

Compile the files in this order and add the ASP.NET Core framework reference:

```xml
<Compile Include="Toolkit/ServiceHealthStore.fs" />
<Compile Include="Toolkit/ServiceHealthCheck.fs" />
<Compile Include="Toolkit/HealthProbes.fs" />
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

- The listener binds `HEALTHCHECK_URLS` if set; otherwise `http://+:8080` in a
  container or Pod and `http://localhost:8080` elsewhere.
- An app that already hosts HTTP maps the endpoints itself with `HealthProbes.map app`.

## Usage

### As Git Submodule

Add to your project:
```bash
git submodule add https://github.com/sebfia/fsharp-toolkit.git src/Toolkit
```

In your `.fsproj`:
```xml
<Compile Include="Toolkit/ConfigurationHelpers.fs" />
```

Update to latest:
```bash
git submodule update --remote src/Toolkit
```

### Examples

```fsharp
open Config

// Parse configuration values
let port = 
    configuration 
    |> tryGetConfigValue "Port"
    |> Option.bind tryParseInt
    |> Option.defaultValue 8080

// Detect containerized environment
let natsUrl = 
    if isRunningInDocker() then "nats://nats:4222"
    else "nats://localhost:4222"
```

## License

MIT
