namespace Wanqix.Services

open System
open System.Collections.Generic
open Wanqix.Core

type VirtualEvent = { Sequence: int64; Kind: string; Target: string }
/// Bounded data-only dispatcher: it never invokes external callbacks with supervisor privileges.
type EventBus() =
    let queue = Queue<VirtualEvent>()
    let mutable dropped = 0L
    member _.Publish event =
        if queue.Count = 1024 then queue.Dequeue() |> ignore; dropped <- dropped + 1L
        queue.Enqueue event
    member _.Drain() =
        let events = queue.ToArray()
        queue.Clear()
        events
    member _.Dropped = dropped

type ServiceManager(guard: Guard, journal: EventJournal, initial: ServiceRecord array, ?lifecycle: LifecycleReporter) =
    let services = Dictionary<string,ServiceRecord>(StringComparer.Ordinal)
    let lifecycle = defaultArg lifecycle (LifecycleReporter())
    static member Defaults =
        [| for id,deps in ["logger",[||];"clock",[|"logger"|];"wvfs",[|"logger"|];"identity",[|"wvfs"|];"metrics",[|"clock";"wvfs"|]] ->
            let capabilities =
                match id with
                | "logger" -> [|"log.read"|]
                | "wvfs" -> [|"filesystem.read";"filesystem.write"|]
                | "identity" -> [|"identity.query";"identity.manage"|]
                | _ -> [|"kernel.inspect"|]
            { Id = id; Dependencies = deps; AutoStart = true; Running = false; Ticks = 0L; LastError = ""; Ring = 1; Capabilities = capabilities } |]
    member private _.Get name = match services.TryGetValue name with true,s -> s | _ -> Errors.fail "WQX-SVC-001" "Servicio inexistente."
    member private this.StartInternal(name,visiting: Set<string>) =
        if visiting.Contains name then Errors.fail "WQX-SVC-002" "Dependencia cíclica."
        let s = this.Get name
        if not s.Running then
            for dep in s.Dependencies do this.StartInternal(dep,visiting.Add name)
            lifecycle.Run("SVC","Iniciar servicio " + name + " (Ring 1)",fun () ->
                services[name] <- { s with Running = true; LastError = "" }
                journal.Write(Info,"ServiceStarted","system",name,"Servicio iniciado en Ring 1."))
    member this.Initialize() =
        let source = if initial.Length = 0 then ServiceManager.Defaults else initial
        for s in source do services.Add(s.Id,{ s with Running = false })
        for s in source do if s.AutoStart then this.StartInternal(s.Id,Set.empty)
    member this.Require(name) = if not (this.Get name).Running then Errors.fail "WQX-SVC-003" ("El servicio " + name + " está detenido.")
    member _.List(token) = guard token "service.query" "services" |> ignore; services.Values |> Seq.sortBy _.Id |> Seq.toArray
    member this.Start(token,name) = guard token "service.manage" name |> ignore; this.StartInternal(name,Set.empty)
    member this.Stop(token,name) =
        guard token "service.manage" name |> ignore
        let s = this.Get name
        if services.Values |> Seq.exists (fun other -> other.Running && Array.contains name other.Dependencies) then Errors.fail "WQX-SVC-002" "Existen dependientes activos."
        services[name] <- { s with Running = false }
        journal.Write(Info,"ServiceStopped","system",name,"Servicio detenido.")
    member this.Restart(token,name) = this.Stop(token,name); this.Start(token,name)
    member this.Pulse(events: VirtualEvent array) =
        for s in services.Values |> Seq.toArray do
            if s.Running then
                try
                    let delta = if s.Id = "logger" then int64 events.Length else 1L
                    services[s.Id] <- { s with Ticks = s.Ticks + delta }
                with ex ->
                    services[s.Id] <- { s with Running = false; LastError = ex.GetType().Name }
                    journal.Write(Error,"ServiceFailed","system",s.Id,ex.GetType().Name)
    member _.IsRunning name = services.ContainsKey name && services[name].Running
    member _.Export() = services.Values |> Seq.sortBy _.Id |> Seq.toArray
    member _.Shutdown(?phase: string) =
        // Reverse dependency order; no external service code is run here.
        for name in ["metrics";"identity";"wvfs";"clock";"logger"] do
            if services.ContainsKey name then
                let s = services[name]
                if s.Running then
                    lifecycle.Run(defaultArg phase "STOP","Detener servicio " + name,fun () ->
                        services[name] <- { s with Running = false }
                        journal.Write(Info,"ServiceStopped","system",name,"Cierre ordenado."))
