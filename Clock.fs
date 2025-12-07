module Clock
open System
open System.Threading
open System.Threading.Tasks
open Polly
open Microsoft.Extensions.Logging
// use a timer and store last handled timespan to avoid time leaks
// change state to mutable task (resize) array
type TaskId = Guid
type ExecutorIndex = Int32
type Time =
    | Every of TimeSpan
    | Once of DateTimeOffset
    | Each of Collections.Generic.IEnumerable<DayOfWeek> * TimeSpan
    //| Cron of Cron.Schedule

type TimedTask = 
    {
        Id: TaskId
        Time: Time
        Task: Async<unit>
        Policy: IAsyncPolicy option
        LastTime: DateTimeOffset option
        NextTime: DateTimeOffset option
    }
    with 
        static member Simple time f = { Id = TaskId.NewGuid(); Time = time; Task = f; Policy = None; LastTime = None; NextTime = None }
        static member New taskId time f policy = { Id = taskId; Time = time; Task = f; Policy = Some policy; LastTime = None; NextTime = None }
        //static member Cron taskId schedule f policy = { Id = taskId; Time = Cron (Cron.create schedule); Task = f; Policy = Some policy; LastTime = None; NextTime = None}
type IClock =
    inherit IDisposable
    abstract member AddTask: TimedTask -> unit
    abstract member RemoveTask: TaskId -> unit
type private HeartbeatMessage =
    | AddTask of TimedTask
    | RemoveTask of TaskId
    | TaskExecuted of ExecutorIndex*TaskExecutedState
    | Heartbeat of from:DateTimeOffset * until:DateTimeOffset
and
    private TaskExecutedState = 
        | TaskFinished of TaskId
        | TaskFailed of TaskId*exn

type private HeartbeatProcessorState = 
    {
        Tasks: TimedTask list
        RunningTasks: TimedTask list
        RemainingTasks: TimedTask list
    }
    with
        static member Initial with get() = { Tasks = []; RunningTasks= []; RemainingTasks = [] }

type private TaskProcessorMessage =
    | ExecuteTask of ExecutorIndex*TimedTask*MailboxProcessor<HeartbeatMessage>

let startClock (logger: ILogger) (heartbeatInterval: int) =
    let updateNextExecuteTime task =
        match task.Time with
        | Every ts ->
            match task.LastTime with
            | Some t -> { task with NextTime = Some (t + ts) }
            | _ -> { task with NextTime = Some (DateTimeOffset.Now + ts) }
        | Once t ->
            match task.LastTime with
            | Some _ -> { task with NextTime = None }
            | _ -> { task with NextTime = Some t }
        | Each (dow,ts) ->
            let now = DateTimeOffset.Now
            let setTime (ts: TimeSpan) (dt: DateTimeOffset) =
                DateTimeOffset(dt.Year, dt.Month, dt.Day, ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds, dt.Offset)
            let nextDate =
                [0..7]
                |> List.map (float >> DateTimeOffset.Now.AddDays >> (setTime ts))
                |> List.filter (fun d -> dow |> Seq.contains d.DayOfWeek && d >= now)
                |> List.min
            logger.LogDebug (sprintf "Updating next execute time of Each-task: %A" nextDate)
            { task with NextTime = nextDate |> Some }
    let chooseTasksInTimeframe (from: DateTimeOffset) until task =
        match task.NextTime with
        | None -> None
        | Some t ->
            //if (t >= from && t <= until) then
            if (t.CompareTo(from) >= 0 && t.CompareTo(until) <= 0) then
                Some task
            else None
    let executeTask (t: TimedTask) =
        let getPolicy =
            function
            | None -> Policy.Handle<Exception>().RetryAsync() :> IAsyncPolicy
            | Some p -> p
        async {
            let policy = getPolicy t.Policy
            let f() = t.Task |> Async.StartAsTask
            let! result = policy.ExecuteAndCaptureAsync(Func<Task<unit>>(f)) |> Async.AwaitTask
            match result.FaultType.HasValue with
            | false -> return (t.Id,None)
            | _ -> return (t.Id, Some result.FinalException)
        }
    let processTask msg =
        async {
            match msg with
            | ExecuteTask (i,t,r) ->
                let! (tid,ex) = executeTask t
                match ex with
                | None -> r.Post(TaskExecuted(i,TaskFinished(tid)))
                | Some x -> r.Post(TaskExecuted(i,TaskFailed(tid,x)))
        }
    let heartbeatProcessor = MailboxProcessor.Start(fun inbox ->
        let taskExecutors = Array.init (Environment.ProcessorCount) (fun _ -> Agent.startStatelessAgent processTask)
        let executorAvailable = Array.init (Environment.ProcessorCount) (fun _ -> true)
        let rec loop (state: HeartbeatProcessorState) =
            async {
                let! newState =
                    inbox.Scan(
                        function
                        | AddTask task ->
                            Some (
                                async {
                                    logger.LogDebug (sprintf "Adding new task %A" task.Id)
                                    let tasks = (task |> updateNextExecuteTime) :: state.Tasks
                                    return { state with Tasks = tasks }
                                }
                            )
                        | RemoveTask tId ->
                            Some (
                                async {
                                    logger.LogDebug (sprintf "Removing task %A" tId)
                                    let tasks =
                                        state.Tasks 
                                        |> List.filter (fun t -> t.Id <> tId) 
                                    return { state with Tasks = tasks }
                                }
                            )
                        | Heartbeat (from,until) ->
                            Some (
                                async {
                                    match state.Tasks |> List.choose (chooseTasksInTimeframe from until) with
                                    | [] -> return state
                                    | toDo ->
                                        logger.LogTrace "We have somthing to do!"
                                        let availableExecutorIds = executorAvailable |> Array.mapi (fun i b -> i,b) |> Array.filter (fun (_,b) -> b) |> Array.map (fun (i,_) -> i)
                                        let toExecute,postponed =
                                            match (toDo |> List.length) > (availableExecutorIds |> Array.length) with
                                            | true -> toDo |> List.splitAt (availableExecutorIds |> Array.length)
                                            | _ -> toDo,[]
                                        toExecute 
                                        |> List.iteri (fun i t -> 
                                            taskExecutors.[(availableExecutorIds.[i])].Post(ExecuteTask(availableExecutorIds.[i],t,inbox)) 
                                            executorAvailable.[(availableExecutorIds.[i])] <- false
                                            )
                                        let tasks = state.Tasks |> List.filter (fun t -> toExecute |> List.map (fun x -> x.Id) |> List.contains t.Id |> not)
                                        return { state with Tasks = tasks; RunningTasks = toExecute; RemainingTasks = state.RemainingTasks |> List.append postponed }
                                }
                            )
                        | TaskExecuted (e,s) ->
                            Some(
                                async {
                                    executorAvailable.[e] <- true
                                    let finishedTask,success =
                                        match s with
                                        | TaskFailed (i,ex) ->
                                            logger.LogError (sprintf "Task with id: %A failed with: %A" i ex)
                                            state.RunningTasks |> Seq.find (fun t -> t.Id = i), false
                                        | TaskFinished i -> 
                                            logger.LogDebug (sprintf "Successfully executed task %A" i) 
                                            state.RunningTasks |> Seq.find (fun t -> t.Id = i), true
                                            //get task from ExecutingTasks in state, update next time an add to tasks
                                    let runnning = state.RunningTasks |> List.filter (fun x -> x.Id <> finishedTask.Id)
                                    let tasks =
                                        if success then
                                            { finishedTask with LastTime = finishedTask.NextTime }
                                            |> updateNextExecuteTime
                                            |> fun t -> 
                                                match t.NextTime with
                                                | Some _ -> t::state.Tasks
                                                | _ -> state.Tasks
                                        else state.Tasks
                                    return
                                        match state.RemainingTasks |> List.tryHead with
                                        | None -> { state with Tasks = tasks; RunningTasks = runnning }
                                        | Some postponed -> 
                                            executorAvailable.[e] <- false
                                            taskExecutors.[e].Post(ExecuteTask(e,postponed,inbox))
                                            { state with Tasks = tasks; RunningTasks = postponed::runnning; RemainingTasks = state.RemainingTasks |> List.filter (fun t -> t.Id <> postponed.Id) }
                                }
                            )
                    )
                return! loop newState
            }
        loop(HeartbeatProcessorState.Initial))
    let onTimer =
        let hbi = heartbeatInterval |> float
        fun _ ->
            let now = DateTimeOffset.Now
            let next = now.AddMilliseconds(hbi - 1.)
            heartbeatProcessor.Post(Heartbeat(now,next))
    let timer = new Timer(TimerCallback(onTimer), null, heartbeatInterval, heartbeatInterval)
    {
        new IClock with
            member __.AddTask task = heartbeatProcessor.Post(AddTask task)
            member __.RemoveTask taskId = heartbeatProcessor.Post(RemoveTask taskId)
            member __.Dispose() = timer.Dispose()
    }