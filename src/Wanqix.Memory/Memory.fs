namespace Wanqix.Memory

open System.Collections.Generic
open Wanqix.Core

type MemoryManager(capacity: int64, guard: Guard, journal: EventJournal) =
    let regions = Dictionary<int,MemoryRegion>()
    let mutable nextId = 0
    let mutable limit = capacity
    do regions.Add(0,{ Id = 0; Start = 0L; Size = 65536L; Pid = 100; Owner = "system"; Ring = Ring.Supervisor })
    let authorize token operation (region: MemoryRegion) =
        let c = guard token operation (string region.Id)
        if region.Ring = Ring.Supervisor || int c.Ring > int region.Ring || (region.Owner <> c.User && not (c.Capabilities.Contains "memory.manage")) then Errors.denied()
        c
    member _.Capacity = limit
    member _.Used = regions.Values |> Seq.sumBy _.Size
    member _.SetCapacity value =
        if regions.Values |> Seq.exists (fun r -> r.Start + r.Size > value) then Errors.fail "WQX-MEM-003" "La capacidad corta una región existente."
        limit <- value
    member _.Map(token) =
        let c = guard token "memory.inspect" "map"
        regions.Values |> Seq.filter (fun r -> r.Owner = c.User || c.Capabilities.Contains "memory.manage") |> Seq.sortBy _.Start |> Seq.toArray
    member _.Allocate(token,pid,owner,ring,processLimit,size) =
        let c = guard token "memory.allocate" (string pid)
        if ring = Ring.Supervisor || int c.Ring > int ring || (c.User <> owner && not (c.Capabilities.Contains "memory.manage")) then Errors.denied()
        let used = regions.Values |> Seq.filter (fun r -> r.Pid = pid) |> Seq.sumBy _.Size
        if size <= 0L || size > limit || size > processLimit - used then Errors.fail "WQX-MEM-003" "Tamaño o límite de proceso inválido."
        let mutable address = 0L
        let mutable found = false
        for r in regions.Values |> Seq.sortBy _.Start do
            if not found then
                if size <= r.Start - address then found <- true
                else address <- r.Start + r.Size
        if size > limit - address then
            journal.Write(Warning,"MemoryPressure",c.User,string pid,"No hay un hueco contiguo suficiente.")
            Errors.fail "WQX-MEM-003" "Memoria virtual insuficiente o fragmentada."
        nextId <- nextId + 1
        let region = { Id = nextId; Start = address; Size = size; Pid = pid; Owner = owner; Ring = ring }
        regions.Add(nextId,region)
        region
    member _.Release(token,id) =
        match regions.TryGetValue id with
        | true,r -> authorize token "memory.allocate" r |> ignore; regions.Remove id |> ignore
        | _ -> Errors.fail "WQX-MEM-003" "Región inexistente."
    /// Trusted supervisor lifecycle hook, never exposed as a syscall or KSL command.
    member _.Reclaim(pid) =
        regions.Values |> Seq.filter (fun r -> r.Pid = pid) |> Seq.map _.Id |> Seq.toArray |> Array.iter (fun id -> regions.Remove id |> ignore)
    member _.LargestFree =
        let mutable previous = 0L
        let mutable largest = 0L
        for r in regions.Values |> Seq.sortBy _.Start do
            largest <- max largest (r.Start - previous)
            previous <- r.Start + r.Size
        max largest (limit - previous)
    member _.Clear() = regions.Clear()
