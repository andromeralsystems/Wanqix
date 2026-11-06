namespace Wanqix.Core

open System
open System.Collections.Generic

type Ring = Supervisor = 0 | System = 1 | Administrative = 2 | User = 3
type KernelState = Cold | Booting | Running | Stopping | Stopped | Failed
type ProcessState = Created | Ready | Running | Waiting | Suspended | Terminated | Faulted
type Severity = Trace | Debug | Info | Warning | Error | Critical
type WqxError = { Code: string; Message: string; Position: int option }
exception WqxException of WqxError

type LifecycleStatus = Starting | Completed | Notice | StepFailed
type LifecycleEvent = { Phase: string; Operation: string; Status: LifecycleStatus; Elapsed: TimeSpan; Detail: string }

/// Trusted composition observer only; failures in presentation never change kernel behavior.
type LifecycleReporter(?observer: LifecycleEvent -> unit) =
    let timer = System.Diagnostics.Stopwatch.StartNew()
    let emit phase operation status detail =
        match observer with
        | Some sink ->
            try sink { Phase = phase; Operation = operation; Status = status; Elapsed = timer.Elapsed; Detail = detail }
            with _ -> ()
        | None -> ()
    member _.Notice(phase,operation,detail) = emit phase operation Notice detail
    member _.Run(phase,operation,action: unit -> 'T) : 'T =
        emit phase operation Starting ""
        try
            let result = action()
            emit phase operation Completed ""
            result
        with ex ->
            let detail = match ex with WqxException error -> error.Code | _ -> ex.GetType().Name
            emit phase operation StepFailed detail
            reraise()

module Errors =
    let fail code message = raise (WqxException { Code = code; Message = message; Position = None })
    let invalid message = fail "WQX-ARG-001" message
    let denied () = fail "WQX-SEC-001" "Acceso denegado."
    let protect f =
        try Ok(f()) with
        | WqxException error -> Result.Error error
        | :? OperationCanceledException -> Result.Error { Code = "WQX-KRN-006"; Message = "Operación cancelada."; Position = None }
        | _ -> Result.Error { Code = "WQX-KRN-500"; Message = "Fallo interno; consulte el registro técnico."; Position = None }

type Cell = Text of string | Number of int64 | Flag of bool
module Cell =
    let text = function Text s -> s | Number n -> string n | Flag b -> string b
    let compare a b =
        match a,b with
        | Number x,Number y -> compare x y
        | Flag x,Flag y -> compare x y
        | _ -> StringComparer.OrdinalIgnoreCase.Compare(text a,text b)
type Table = { Columns: string list; Rows: Map<string,Cell> list }
module Table =
    let create columns rows = { Columns = columns; Rows = rows |> List.map (List.zip columns >> Map.ofList) }
    let message text = create ["result"] [[Text text]]

[<CLIMutable>]
type LogEntry = { Sequence: int64; Time: DateTimeOffset; Level: string; Kind: string; Actor: string; Target: string; Message: string }
type IClock = abstract UtcNow: DateTimeOffset
type SystemClock() = interface IClock with member _.UtcNow = DateTimeOffset.UtcNow
type ManualClock(start: DateTimeOffset) =
    let mutable now = start
    member _.Advance(duration: TimeSpan) = now <- now + duration
    interface IClock with member _.UtcNow = now

type EventJournal(clock: IClock, ?initial: LogEntry array) =
    let entries = Queue<LogEntry>(defaultArg initial [||])
    let mutable sequence = if entries.Count = 0 then 0L else entries |> Seq.map _.Sequence |> Seq.max
    member _.Write(level: Severity, kind, actor, target, message) =
        sequence <- sequence + 1L
        let entry = { Sequence = sequence; Time = clock.UtcNow; Level = string level; Kind = kind; Actor = actor; Target = target; Message = message }
        entries.Enqueue entry
        while entries.Count > 4096 do entries.Dequeue() |> ignore
    member _.Entries = entries.ToArray()
    member _.Sequence = sequence
    member _.After(previous) = entries |> Seq.filter (fun e -> e.Sequence > previous) |> Seq.toArray

type SecurityContext = { User: string; Ring: Ring; Capabilities: Set<string>; Session: Guid }
type Guard = Guid -> string -> string -> SecurityContext

[<CLIMutable>]
type UserRecord = { Id: Guid; Name: string; Roles: string array; Groups: string array; Salt: string; Hash: string; Iterations: int; Disabled: bool }
[<CLIMutable>]
type RoleRecord = { Name: string; Capabilities: string array }
[<CLIMutable>]
type KernelConfig = { MemoryCapacity: int64; DefaultProcessLimit: int64; TickMilliseconds: int; HistoryEnabled: bool; Color: bool }
module KernelConfig =
    let defaults = { MemoryCapacity = 512L * 1024L * 1024L; DefaultProcessLimit = 64L * 1024L * 1024L; TickMilliseconds = 50; HistoryEnabled = true; Color = true }
    let validate c =
        if c.MemoryCapacity < 1048576L || c.MemoryCapacity > 1099511627776L || c.DefaultProcessLimit < 1L || c.DefaultProcessLimit > c.MemoryCapacity || c.TickMilliseconds < 10 || c.TickMilliseconds > 10000 then
            Errors.fail "WQX-CFG-001" "Configuración fuera de límites."
        c

type MemoryRegion = { Id: int; Start: int64; Size: int64; Pid: int; Owner: string; Ring: Ring }
type ProcessInfo = { Pid: int; Name: string; Owner: string; Ring: Ring; State: ProcessState; Priority: int; Created: DateTimeOffset; Runtime: int64; MemoryLimit: int64; Capabilities: Set<string>; Remaining: int; Diagnostic: string }
[<CLIMutable>]
type FileEntry = { Path: string; Directory: bool; Owner: string; PublicRead: bool; Content: string; Modified: DateTimeOffset }
[<CLIMutable>]
type ServiceRecord = { Id: string; Dependencies: string array; AutoStart: bool; Running: bool; Ticks: int64; LastError: string; Ring: int; Capabilities: string array }

type ProcessAction = Pause | Resume | End
type Request =
    | KernelInspect | KernelShutdown | KernelReboot
    | ConfigGet | ConfigSet of key:string * value:string
    | ProcessList | ProcessCreate of name:string * priority:int * memoryLimit:int64 * steps:int
    | ProcessControl of pid:int * action:ProcessAction
    | SchedulerTick of count:int
    | MemoryMap | MemoryAllocate of pid:int * size:int64 | MemoryRelease of region:int
    | FileList of path:string | FileRead of path:string | FileWrite of path:string * content:string
    | DirectoryCreate of path:string | FileDelete of path:string | FileMove of source:string * target:string
    | FilePermission of path:string * publicRead:bool
    | ServiceList | ServiceStart of name:string | ServiceStop of name:string | ServiceRestart of name:string
    | DeviceList | DeviceInfo of name:string
    | IdentityQuery | IdentityList | IdentityCreate of name:string * password:string
    | IdentityGrant of name:string * role:string | IdentityRevoke of name:string * role:string
    | LogRead of audit:bool

/// Future external modules must use a validated protocol in a separate OS process.
/// No in-process assembly loader or arbitrary host-command executor is provided.
type IIsolatedModuleTransport =
    abstract Invoke: moduleId:string * request:byte array * cancellation:System.Threading.CancellationToken -> System.Threading.Tasks.Task<byte array>

type IKernel =
    abstract Call: session:Guid * request:Request -> Result<Table,WqxError>
    abstract State: KernelState
