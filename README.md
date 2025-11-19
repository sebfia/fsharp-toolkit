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
