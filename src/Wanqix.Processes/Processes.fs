namespace Wanqix.Processes

open System
open System.Collections.Generic
open Wanqix.Core

type ProcessManager(guard: Guard, clock: IClock, journal: EventJournal, reclaim: int -> unit) =
    let processes = Dictionary<int,ProcessInfo>()
    let mutable nextPid = 100
    let mutable cursor = 0
    let mutable ticks = 0L
    do processes.Add(100,{ Pid = 100; Name = "supervisor"; Owner = "system"; Ring = Ring.Supervisor; State = ProcessState.Running; Priority = 9; Created = clock.UtcNow; Runtime = 0L; MemoryLimit = 65536L; Capabilities = Set.empty; Remaining = 0; Diagnostic = "Kernel lifecycle and syscall dispatcher" })
    let get pid = match processes.TryGetValue pid with true,p -> p | _ -> Errors.fail "WQX-PROC-002" "Proceso inexistente."
    let active p = p.State <> ProcessState.Terminated && p.State <> ProcessState.Faulted
    member _.List(token) =
        let c = guard token "process.list" "processes"
        processes.Values |> Seq.filter (fun p -> p.Owner = c.User || c.Capabilities.Contains "process.manage") |> Seq.sortBy _.Pid |> Seq.toArray
    member _.GetAuthorized(token,pid,operation) =
        let c = guard token operation (string pid)
        let p = get pid
        if p.Ring = Ring.Supervisor || int c.Ring > int p.Ring || (p.Owner <> c.User && not (c.Capabilities.Contains "process.manage")) then Errors.denied()
        if not (active p) then Errors.fail "WQX-PROC-003" "El proceso ya terminó."
        p
    member _.Create(token,name,priority,memoryLimit,steps,maxLimit) =
        let c = guard token "process.create" name
        if String.IsNullOrWhiteSpace name || name.Length > 64 || name |> Seq.exists Char.IsControl || priority < 0 || priority > 9 || steps < 1 || steps > 1000000 || memoryLimit < 1L || memoryLimit > maxLimit then Errors.invalid "Parámetros de proceso inválidos."
        if processes.Values |> Seq.filter active |> Seq.length >= 1024 then Errors.fail "WQX-PROC-004" "Límite de procesos activos alcanzado."
        // Reap old tombstones; PID allocation remains monotonically increasing.
        if processes.Count >= 4096 then
            processes.Values |> Seq.filter (active >> not) |> Seq.sortBy _.Pid |> Seq.truncate 1024 |> Seq.map _.Pid |> Seq.toArray |> Array.iter (fun pid -> processes.Remove pid |> ignore)
        nextPid <- nextPid + 1
        let p = { Pid = nextPid; Name = name; Owner = c.User; Ring = Ring.User; State = ProcessState.Created; Priority = priority; Created = clock.UtcNow; Runtime = 0L; MemoryLimit = memoryLimit; Capabilities = Set.intersect c.Capabilities (Set.ofList ["filesystem.read";"filesystem.write";"memory.allocate"]); Remaining = steps; Diagnostic = "counter=0" }
        processes.Add(p.Pid,{ p with State = ProcessState.Ready })
        journal.Write(Info,"ProcessStarted",c.User,string p.Pid,name)
        p.Pid
    member this.Control(token,pid,action) =
        let p = this.GetAuthorized(token,pid,"process.terminate")
        let state =
            match action,p.State with
            | End,_ -> reclaim pid; ProcessState.Terminated
            | Pause,(ProcessState.Ready | ProcessState.Waiting) -> ProcessState.Suspended
            | Resume,ProcessState.Suspended -> ProcessState.Ready
            | _ -> Errors.fail "WQX-PROC-003" "Transición de proceso inválida."
        processes[pid] <- { p with State = state }
        journal.Write(Info,(if action = End then "ProcessTerminated" else "ProcessStateChanged"),p.Owner,string pid,string state)
    member _.Tick() =
        ticks <- ticks + 1L
        let supervisor = processes[100]
        processes[100] <- { supervisor with Runtime = ticks }
        // Waiting tasks are signalled at the next logical tick; no busy waiting.
        for p in processes.Values |> Seq.toArray do
            if p.State = ProcessState.Waiting && p.Ring <> Ring.Supervisor then processes[p.Pid] <- { p with State = ProcessState.Ready }
        let ready = processes.Values |> Seq.filter (fun p -> p.State = ProcessState.Ready) |> Seq.sortBy _.Pid |> Seq.toArray
        if ready.Length > 0 then
            // Weighted round robin: priority 0..9 receives 1..10 work units per turn.
            cursor <- cursor % ready.Length
            let p = ready[cursor]
            cursor <- cursor + 1
            processes[p.Pid] <- { p with State = ProcessState.Running }
            try
                let work = min p.Remaining (p.Priority + 1)
                let remaining = p.Remaining - work
                let runtime = p.Runtime + int64 work
                let state = if remaining = 0 then ProcessState.Terminated elif ticks % 4L = 0L then ProcessState.Waiting else ProcessState.Ready
                processes[p.Pid] <- { p with Runtime = runtime; Remaining = remaining; State = state; Diagnostic = "counter=" + string runtime }
                if remaining = 0 then
                    reclaim p.Pid
                    journal.Write(Info,"ProcessTerminated",p.Owner,string p.Pid,"Trabajo completado.")
            with ex ->
                processes[p.Pid] <- { p with State = ProcessState.Faulted; Diagnostic = ex.GetType().Name }
                reclaim p.Pid
                journal.Write(Error,"ProcessFaulted",p.Owner,string p.Pid,ex.GetType().Name)
    member _.Shutdown(?lifecycle: LifecycleReporter, ?phase: string) =
        let lifecycle = defaultArg lifecycle (LifecycleReporter())
        for p in processes.Values |> Seq.toArray do
            if active p then
                lifecycle.Run(defaultArg phase "STOP",sprintf "Finalizar proceso virtual PID %d (%s) y reclamar regiones" p.Pid p.Name,fun () ->
                    reclaim p.Pid
                    processes[p.Pid] <- { p with State = ProcessState.Terminated }
                    journal.Write(Info,"ProcessTerminated",p.Owner,string p.Pid,"Apagado del kernel."))
