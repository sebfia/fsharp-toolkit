namespace Mercator

[<AutoOpen>]
module FSPolly =
    
    open System
    open Polly

    let inline orException<'TException when 'TException :> exn> (pb: PolicyBuilder) =
        pb.Or<'TException>()
    let inline waitAndRetryAsync (times: TimeSpan list) (pb: PolicyBuilder) =
        pb.WaitAndRetryAsync(times)


module Metronome =

    open System
    open Microsoft.Extensions.Hosting
    open Microsoft.Extensions.Logging
    open Microsoft.Extensions.Configuration
    open Microsoft.Extensions.DependencyInjection
    open Polly
    open System.Threading
    open System.Threading.Tasks

    let doWork (ct: CancellationToken) (logger: ILogger) (tasks: Clock.TimedTask array) = 
        let waitHandle = new ManualResetEvent(false)
        async {
            ct.Register(fun _ ->
                waitHandle.Set() |> ignore
            ) |> ignore

            logger.LogInformation "Metronome service starting"

            let clock = Clock.startClock logger 100

            tasks |> Array.iter clock.AddTask

            logger.LogInformation "Metronome service has been started."

            waitHandle.WaitOne() |> ignore

            logger.LogInformation "Metronome service is shutting down"

            logger.LogDebug "Disposing of resources"

            waitHandle.Dispose()

            clock.Dispose()

            logger.LogInformation "Metronome service is stopped."
        }

    let tryParseDayOfWeek (s: string) = 
        let fullDay =
            function
            | "MON" -> "Monday" |> Some
            | "TUE" -> "Tuesday" |> Some
            | "WED" -> "Wednesday" |> Some
            | "THU" -> "Thursday" |> Some
            | "FRI" -> "Friday" |> Some
            | "SAT" -> "Saturday" |> Some
            | "SUN" -> "Sunday" |> Some
            | _ -> None
        match s.ToUpper() |> fullDay with
        | None -> None
        | Some fd -> 
            match Enum.TryParse<DayOfWeek>(fd) with
            | (true,dow) -> Some dow
            | _ -> None

    let tryParseTime s =
        match DateTime.TryParseExact(s, "HH:mm:ss", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | (true, dt) -> Some (dt.TimeOfDay)
        | _ -> None
    

    type ITaskCreator =
        abstract CreateTasks: unit -> Clock.TimedTask array

    type TaskCreator(logger: ILogger<TaskCreator>, config: IConfiguration, sp: IServiceProvider) =

        let defaultRetryPolicy = Polly.Policy.Handle<Exception>().WaitAndRetryAsync([5 |> seconds; 10 |> seconds; 30 |> seconds; 10 |> minutes; 10 |> minutes; 10 |> minutes; 30 |> minutes; 1 |> hours])

        interface ITaskCreator with
            member __.CreateTasks() = 
                logger.LogInformation "Setting up timed tasks."

                let tasks = sp.GetServices<ScheduledTasks.ITask>()
                
                let numTasks = tasks |> Seq.length
                logger.LogTrace $"Got {numTasks} tasks to queue."

                [| for task in tasks do
                    logger.LogTrace $"Reading schedule for {task.Name} task."
                    
                    let days = config |> Config.tryGetSectionArray task.Name "Days" |> Option.map (Array.map tryParseDayOfWeek >> Array.choose id) |> Option.defaultValue [||]
                    let time = config |> Config.tryGetSectionValue task.Name "Time" |> Option.map (tryParseTime >> Option.defaultValue TimeSpan.Zero) |> Option.defaultValue TimeSpan.Zero 

                    logger.LogInformation (sprintf "Schedule for %s-task is at %A on each %s" task.Name time (days |> Array.map (sprintf "%A") |> String.concat " and "))

                    let schedule = Clock.Each (days, time)

                    let timedTask = Clock.TimedTask.New (Clock.TaskId.NewGuid()) schedule task.Run defaultRetryPolicy

                    timedTask
                |]
                    

                // [|
                //     simpleTask
                // |]

    type MetronomeService(logger: ILogger<MetronomeService>, config: IConfiguration, taskCreator: ITaskCreator) =
        inherit BackgroundService()

        override __.ExecuteAsync ct = 
            task {
                // config.AsEnumerable() |> Seq.iter (fun kvp -> sprintf "%s : %s" kvp.Key kvp.Value |> logger.LogDebug)
                logger.LogInformation (sprintf "QuestDb location: %s" (config.["QuestDb"]))
                Async.Start ((doWork ct (logger :> ILogger) (taskCreator.CreateTasks())), ct)
            } :> Task