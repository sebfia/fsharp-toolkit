module Config
open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Extensions.Configuration


let inline tryGetEnvironmentVariable name =
    match Environment.GetEnvironmentVariable name with
    | x when String.IsNullOrEmpty x -> None
    | s -> Some s

let inline tryGetConfigValue key (c: IConfiguration) =
    if isNull c then None else
    match c.[key] with
    | s when (String.IsNullOrWhiteSpace s |> not) -> Some (Regex.Replace(s, @"\s+", ""))
    | _ -> None

let inline tryGetConfigValueWithDefault<'a> key (defaultValue: 'a) (c: IConfiguration) =
    if isNull c then defaultValue else
    try 
        c.GetValue<'a>(key, defaultValue)
    with _ -> defaultValue

let inline tryGetSectionValue section key (c: IConfiguration) =
    if isNull c then None else
    try 
        c.GetSection(sprintf "%s:%s" section key).Value |> Some
    with _ -> None

let inline tryGetSectionArray section key (c: IConfiguration) =
    if isNull c then None else
    try
        c.GetSection(sprintf "%s:%s" section key).GetChildren() |> Seq.map (fun x -> x.Value) |> Seq.toArray |> Some
    with _ -> None

let inline tryGetSectionMap<'key,'value> section key mapKeyName mapValueName (c: IConfiguration) =
    if isNull c then None else
    try
        c.GetSection(sprintf "%s:%s" section key).GetChildren() |> Seq.map (fun x -> x.GetValue<'key>(mapKeyName),x.GetValue<'value>(mapValueName)) |> Seq.toArray |> Some
    with _ -> None

let inline tryGetSectionObjectArray<'T> section key (mapper: IConfigurationSection -> 'T option) (c: IConfiguration) =
    if isNull c then None else
    try
        let sectionPath = sprintf "%s:%s" section key
        let children = c.GetSection(sectionPath).GetChildren() |> Seq.toList
        if List.isEmpty children then None else
        children
        |> List.choose mapper
        |> function
            | [] -> None
            | items -> Some items
    with _ -> None

let inline enumToList<'a> = (Enum.GetValues(typeof<'a>) :?> ('a [])) |> Array.toList

let inline private mapTryFindReplacement (m: Text.RegularExpressions.Match) map : string option =
    let s = sprintf "%A" m
    let key = (s.Replace("{", "")).Replace("}", "")
    Map.tryFind key map

let inline replacer map  =
    fun (m: Text.RegularExpressions.Match) -> mapTryFindReplacement m map |> function | None -> "" | Some x -> x

let inline expandTemplate t =
    let specialFolders =  enumToList<Environment.SpecialFolder> |> List.map (fun v -> (sprintf "%A" v),Environment.GetFolderPath v)
    let regex = Regex(@"\{\w+\}", RegexOptions.Compiled ||| RegexOptions.Singleline ||| RegexOptions.CultureInvariant)
    fun expansions ->
        let map = List.concat [specialFolders; expansions] |> Map.ofList
        regex.Replace(t, (replacer map))

let inline adaptOSDirectorySeparator (s: string) =
        match Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.Windows) with
        | true -> s.Replace('/', Path.DirectorySeparatorChar)
        | _ -> s.Replace('\\', Path.DirectorySeparatorChar)

let inline tryParseInt (str: string) =
    match Int32.TryParse str with
    | (true, i) -> Some i
    | _ -> None

let inline tryParseInt64 (str: string) =
    match Int64.TryParse str with
    | (true, i) -> Some i
    | _ -> None

let inline tryParseBool (str: string) =
    match Boolean.TryParse str with
    | (true, b) -> Some b
    | _ -> None

let inline tryParseFloat (str: string) =
    match Double.TryParse str with
    | (true, f) -> Some f
    | _ -> None

/// <summary>
/// Detects if the application is running inside a Docker container or Kubernetes pod.
/// Checks for the presence of the /.dockerenv file (Docker) or for "docker"/"kubepods" in /proc/1/cgroup (Docker/K8s).
/// Returns true if running in a containerized environment, false otherwise.
/// </summary>
let inline isRunningInDocker() =
    File.Exists("/.dockerenv") ||
    (File.Exists("/proc/1/cgroup") &&
     let content = File.ReadAllText("/proc/1/cgroup")
     content.Contains("docker") || content.Contains("kubepods"))

/// Parse QuestDB connection string (format: "http://host:port")
/// Returns tuple of (baseUrl, ingressPort) with defaults if parsing fails
let inline parseQuestDbConnectionString connectionString =
    try
        let uri = Uri connectionString
        let baseUrl = sprintf "%s://%s" uri.Scheme uri.Host
        let port = if uri.Port > 0 then uri.Port else 9009
        baseUrl, port
    with _ ->
        "http://localhost", 9009

/// Get QuestDB connection from config
/// Priority: 1) ConnectionStrings:questdb, 2) Legacy QuestDb + QuestDbIngressPort, 3) Default
let inline getQuestDbConnection (c: IConfiguration) =
    match c.["ConnectionStrings:questdb"] |> Option.ofObj with
    | Some connStr -> parseQuestDbConnectionString connStr
    | None ->
        // Legacy fallback
        let baseUrl = c |> tryGetConfigValue "QuestDb" |> Option.defaultValue "http://localhost"
        let port = c |> tryGetConfigValueWithDefault "QuestDbIngressPort" 9009
        baseUrl, port
