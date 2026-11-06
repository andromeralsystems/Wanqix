namespace Wanqix.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Wanqix.Core
open Wanqix.Security
open Wanqix.Memory
open Wanqix.Processes
open Wanqix.Storage

[<CLIMutable>]
type Snapshot = { Version: int; Config: KernelConfig; Users: UserRecord array; Roles: RoleRecord array; Files: FileEntry array; Services: ServiceRecord array; Journal: LogEntry array; History: string array; BootCount: int64 }

type private KernelMessage = Execute of (unit -> obj) * TaskCompletionSource<Result<obj,exn>>

/// A single writer mailbox makes syscalls atomic with respect to scheduler ticks.
/// No global locks, no untrusted code callbacks, no terminal dependency.
type Kernel private (store: IStateStore, clock: IClock, initial: Snapshot option, password: string, recovered: bool, lifecycle: LifecycleReporter) as this =
    let initialConfig = initial |> Option.map _.Config |> Option.defaultValue KernelConfig.defaults
    let mutable config = lifecycle.Run("BOOT","Validar configuración del kernel",fun () -> KernelConfig.validate initialConfig)
    let journal = lifecycle.Run("BOOT","Inicializar registro de eventos y auditoría",fun () -> EventJournal(clock, ?initial = (initial |> Option.map _.Journal)))
    let users = lifecycle.Run("BOOT","Cargar identidades virtuales y credenciales",fun () -> initial |> Option.map _.Users |> Option.defaultWith (fun () -> [|Authority.CreateUser("root",password,[|"admin"|]); Authority.CreateUser("guest",Guid.NewGuid().ToString("N"),[|"user"|])|]))
    let authority = lifecycle.Run("BOOT","Inicializar Wanqix Security Authority",fun () -> Authority(journal,users,initial |> Option.map _.Roles |> Option.defaultValue Authority.DefaultRoles,clock))
    let guard : Guard = fun token op target -> authority.Authorize token op target
    let memory = lifecycle.Run("BOOT",sprintf "Inicializar memoria lógica (%d bytes); reservar supervisor" config.MemoryCapacity,fun () -> MemoryManager(config.MemoryCapacity,guard,journal))
    let processes = lifecycle.Run("BOOT","Crear tabla de procesos, PID 100 y planificador",fun () -> ProcessManager(guard,clock,journal,memory.Reclaim))
    let files = lifecycle.Run("BOOT","Montar y validar el árbol del sistema de archivos virtual",fun () -> VirtualFileSystem(guard,clock,journal,initial |> Option.map _.Files |> Option.defaultValue [||]))
    let services = lifecycle.Run("BOOT","Crear administrador de servicios",fun () -> ServiceManager(guard,journal,initial |> Option.map _.Services |> Option.defaultValue [||],lifecycle = lifecycle))
    let serviceTokens = lifecycle.Run("BOOT","Registrar capacidades privadas de servicios Ring 1",fun () -> ServiceManager.Defaults |> Array.map (fun s -> s.Id,authority.RegisterSystemService(s.Id,s.Capabilities)) |> Map.ofArray)
    let bus = lifecycle.Run("BOOT","Inicializar bus de eventos virtuales",fun () -> EventBus())
    let history = lifecycle.Run("BOOT","Restaurar historial de comandos",fun () -> ResizeArray<string>(initial |> Option.map _.History |> Option.defaultValue [||]))
    let mutable state = KernelState.Cold
    let mutable reboot = false
    let bootCount = 1L + (initial |> Option.map _.BootCount |> Option.defaultValue 0L)
    let mutable lastDispatched = 0L
    let mutable tickCount = 0L
    let inbox = new BlockingCollection<KernelMessage>(1024)
    let mutable disposed = false
    // A dedicated, sleeping consumer also progresses when all thread-pool callers await replies.
    let supervisorThread = Thread(ThreadStart(fun () ->
        for Execute(action,reply) in inbox.GetConsumingEnumerable() do
            try reply.SetResult(Ok(action())) with ex -> reply.SetResult(Result.Error ex)))
    do supervisorThread.IsBackground <- true; supervisorThread.Name <- "Wanqix supervisor"
    let serial (f: unit -> 'T) : 'T =
        if disposed then Errors.fail "WQX-KRN-005" "El kernel fue descargado."
        let reply = TaskCompletionSource<Result<obj,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        inbox.Add(Execute((fun () -> box(f())),reply))
        match reply.Task.GetAwaiter().GetResult() with
        | Ok value -> unbox<'T> value
        | Result.Error ex -> raise ex
    let requireRunning () = if state <> KernelState.Running then Errors.fail "WQX-KRN-005" "El kernel no acepta operaciones en su estado actual."
    let table columns rows = Table.create columns rows
    let snapshot () =
        { Version = 1; Config = config; Users = authority.ExportUsers(); Roles = authority.ExportRoles(); Files = files.Export(); Services = services.Export(); Journal = journal.Entries; History = history.ToArray(); BootCount = bootCount }
    let save () = store.Save(snapshot())
    let tick () =
        if state = KernelState.Running then
            tickCount <- tickCount + 1L
            if journal.Sequence > lastDispatched then
                for entry in journal.After lastDispatched do bus.Publish { Sequence = entry.Sequence; Kind = entry.Kind; Target = entry.Target }
                lastDispatched <- journal.Sequence
            if services.IsRunning "clock" then processes.Tick()
            if services.IsRunning "metrics" && tickCount % 20L = 0L then this.Dispatch(serviceTokens["metrics"],KernelInspect) |> ignore
            services.Pulse(bus.Drain())
    let shutdown () =
        if state = KernelState.Running then
            let phase = if reboot then "REBOOT" else "STOP"
            lifecycle.Notice(phase,"Solicitud aceptada",if reboot then "Reinicio del entorno virtual." else "Apagado del entorno virtual.")
            try
                lifecycle.Run(phase,"Detener admisión de llamadas al sistema",fun () ->
                    state <- KernelState.Stopping
                    journal.Write(Info,"KernelShutdownRequested","system","kernel","Se detiene la admisión de llamadas."))
                lifecycle.Run(phase,"Finalizar procesos virtuales y reclamar sus regiones",fun () -> processes.Shutdown(lifecycle = lifecycle,phase = phase))
                lifecycle.Run(phase,"Vaciar tabla de regiones de memoria lógica",memory.Clear)
                services.Shutdown(phase = phase)
                lifecycle.Run(phase,"Revocar sesiones y tokens internos",authority.Close)
                journal.Write(Info,"KernelStopping","system","kernel","Recursos liberados; guardando estado.")
                lifecycle.Run(phase,"Guardar snapshot atómico del sistema",save)
                lifecycle.Run(phase,"Establecer estado Stopped",fun () -> state <- KernelState.Stopped)
                lifecycle.Notice(phase,"Kernel detenido",if reboot then "Listo para comenzar otro arranque." else "Apagado virtual completado.")
            with ex ->
                state <- KernelState.Failed
                journal.Write(Critical,"ShutdownFailed","system","kernel",ex.GetType().Name)
                reraise()
    do
        state <- KernelState.Booting
        journal.Write(Info,"KernelBoot","system","runtime","Runtime .NET inicializado; configuración validada.")
        if recovered then
            journal.Write(Warning,"StateRecovered","system","state","Restaurado el último respaldo válido.")
            lifecycle.Notice("BOOT","Recuperación de persistencia","Se cargó el último respaldo válido; se conserva hasta la próxima escritura.")
        services.Initialize()
        lifecycle.Run("BOOT","Comprobar directorios personales de usuarios",fun () -> for u in users do files.EnsureHome u.Name)
        // Never rotate a corrupt primary over the valid recovery backup.
        if not recovered then lifecycle.Run("BOOT","Guardar estado inicial mediante snapshot atómico",save)
        lifecycle.Run("BOOT","Iniciar hilo supervisor y habilitar WSCI",fun () ->
            supervisorThread.Start()
            state <- KernelState.Running)
        journal.Write(Info,"KernelRunning","system","kernel","Servicios esenciales e identidades disponibles.")
        lifecycle.Notice("BOOT","Kernel preparado",sprintf "Arranque %d; servicios e identidades disponibles." bootCount)
    static member Boot(store: IStateStore, ?clock: IClock, ?initialRootPassword: string, ?lifecycle: LifecycleReporter) =
        let lifecycle = defaultArg lifecycle (LifecycleReporter())
        let clock = defaultArg clock (SystemClock() :> IClock)
        let validate (s: Snapshot) =
            if isNull (box s) || s.Version <> 1 || isNull (box s.Config) || isNull s.Users || isNull s.Roles || isNull s.Files || isNull s.Services || isNull s.Journal || isNull s.History then Errors.fail "WQX-STO-001" "Formato de persistencia inválido."
            KernelConfig.validate s.Config |> ignore
            if s.Users.Length < 2 || s.Users.Length > 10000 || s.Files.Length > 10000 || s.Journal.Length > 4096 || s.History.Length > 500 || s.Roles.Length > 32 then Errors.fail "WQX-STO-001" "Límites de persistencia inválidos."
            for u in s.Users do
                if isNull u.Roles || isNull u.Groups || u.Iterations < 210000 || u.Iterations > 1000000 || Convert.FromBase64String(u.Salt).Length <> 32 || Convert.FromBase64String(u.Hash).Length <> 32 || not (System.Text.RegularExpressions.Regex.IsMatch(u.Name,"^[a-z][a-z0-9._-]{0,31}$")) then Errors.fail "WQX-STO-001" "Identidad inválida."
            let names = s.Users |> Array.map _.Name |> Set.ofArray
            if names.Count <> s.Users.Length || not (names.Contains "root" && names.Contains "guest") then Errors.fail "WQX-STO-001" "Identidades duplicadas o ausentes."
            let knownCaps = Authority.DefaultRoles |> Seq.collect _.Capabilities |> Set.ofSeq
            for role in s.Roles do if isNull role.Capabilities || role.Capabilities |> Array.exists (knownCaps.Contains >> not) then Errors.fail "WQX-STO-001" "Capacidades persistentes desconocidas."
            let defaults = ServiceManager.Defaults
            if s.Services.Length <> defaults.Length then Errors.fail "WQX-STO-001" "Registro de servicios inválido."
            for d in defaults do
                match s.Services |> Array.tryFind (fun s -> s.Id = d.Id) with
                | Some s when s.Dependencies = d.Dependencies && s.Ring = 1 && s.Capabilities = d.Capabilities -> ()
                | _ -> Errors.fail "WQX-STO-001" "Dependencias de servicios inválidas."
            VirtualFileSystem((fun _ _ _ -> Errors.denied()),clock,EventJournal(clock),s.Files) |> ignore
        let initial,recovered = lifecycle.Run("BOOT","Leer y validar persistencia (primario / respaldo)",fun () -> store.Load<Snapshot> validate)
        lifecycle.Notice("BOOT","Origen del estado",if recovered then "Respaldo validado." elif initial.IsSome then "Snapshot primario validado." else "Primer arranque: crear estado inicial.")
        new Kernel(store,clock,initial,defaultArg initialRootPassword "",recovered,lifecycle)
    member _.State = serial(fun () -> state)
    member _.Config = serial(fun () -> config)
    member _.RebootRequested = serial(fun () -> reboot)
    member _.Guest() = serial(fun () -> requireRunning(); authority.Guest())
    member _.Login(name,password) = serial(fun () -> Errors.protect(fun () -> requireRunning(); services.Require "identity"; authority.Login(name,password)))
    member _.Logout(token) = serial(fun () -> authority.Logout token)
    member _.Session(token) = serial(fun () -> Errors.protect(fun () -> requireRunning(); authority.Context token))
    member _.History = serial(fun () -> history.ToArray())
    member _.Remember(command: string) = serial(fun () ->
        if config.HistoryEnabled && command.Length <= 4096 && not (command.Contains("usr.login",StringComparison.OrdinalIgnoreCase) || command.Contains("usr.new",StringComparison.OrdinalIgnoreCase) || command.Contains("password",StringComparison.OrdinalIgnoreCase)) then
            history.Add command
            while history.Count > 500 do history.RemoveAt 0)
    member _.Checkpoint() = serial(fun () -> requireRunning(); save())
    member _.Tick() = serial tick
    member _.Shutdown() = serial shutdown
    member private _.Dispatch(token,request) =
        let mutable actor = "unknown"
        try
            Errors.protect(fun () ->
                requireRunning()
                let ctx = authority.Context token
                actor <- ctx.User
                let ok () = Table.message "OK"
                let fsReady () =
                    services.Require "wvfs"
                    guard serviceTokens["wvfs"] "filesystem.read" "WVFS service boundary" |> ignore
                match request with
                | KernelInspect ->
                    guard token "kernel.inspect" "kernel" |> ignore
                    table ["version";"state";"boot";"memory.used";"memory.capacity";"memory.largestFree";"events.dropped"] [[Text "1.0.0";Text(string state);Number bootCount;Number memory.Used;Number memory.Capacity;Number memory.LargestFree;Number bus.Dropped]]
                | KernelShutdown | KernelReboot ->
                    guard token "kernel.shutdown" "kernel" |> ignore
                    reboot <- request = KernelReboot
                    shutdown(); ok()
                | ConfigGet ->
                    guard token "kernel.inspect" "config" |> ignore
                    table ["key";"value"] [[Text "memory.capacity";Number config.MemoryCapacity];[Text "process.limit";Number config.DefaultProcessLimit];[Text "tick.ms";Number(int64 config.TickMilliseconds)];[Text "history";Flag config.HistoryEnabled];[Text "color";Flag config.Color]]
                | ConfigSet(key,value) ->
                    guard token "kernel.configure" key |> ignore
                    let integer () = match Int64.TryParse value with true,n -> n | _ -> Errors.invalid "Se esperaba un entero."
                    let boolean () = match Boolean.TryParse value with true,b -> b | _ -> Errors.invalid "Se esperaba true/false."
                    let next =
                        match key with
                        | "memory.capacity" -> { config with MemoryCapacity = integer() }
                        | "process.limit" -> { config with DefaultProcessLimit = integer() }
                        | "tick.ms" ->
                            let n = integer()
                            if n < 10L || n > 10000L then Errors.invalid "tick.ms fuera de rango."
                            { config with TickMilliseconds = int n }
                        | "history" -> { config with HistoryEnabled = boolean() }
                        | "color" -> { config with Color = boolean() }
                        | _ -> Errors.invalid "Clave de configuración desconocida."
                    KernelConfig.validate next |> ignore
                    memory.SetCapacity next.MemoryCapacity
                    config <- next
                    journal.Write(Warning,"ConfigChanged",ctx.User,key,value)
                    ok()
                | ProcessList ->
                    processes.List token |> Array.map (fun p -> [Number(int64 p.Pid);Text p.Name;Text p.Owner;Number(int64(int p.Ring));Text(string p.State);Number(int64 p.Priority);Number p.Runtime;Number p.MemoryLimit;Text(p.Created.ToString("O"));Text(String.concat "," p.Capabilities);Text p.Diagnostic]) |> Array.toList |> table ["pid";"name";"owner";"ring";"state";"priority";"runtime";"memory.limit";"created";"capabilities";"diagnostic"]
                | ProcessCreate(name,priority,limit,steps) ->
                    let maxLimit = if ctx.Capabilities.Contains "process.manage" then config.MemoryCapacity else config.DefaultProcessLimit
                    let pid = processes.Create(token,name,priority,limit,steps,maxLimit)
                    table ["pid"] [[Number(int64 pid)]]
                | ProcessControl(pid,action) -> processes.Control(token,pid,action); ok()
                | SchedulerTick count ->
                    guard token "kernel.configure" "scheduler" |> ignore
                    if count < 1 || count > 10000 then Errors.invalid "Cantidad de ticks fuera de rango."
                    for _ in 1..count do tick()
                    ok()
                | MemoryMap ->
                    memory.Map token |> Array.map (fun r -> [Number(int64 r.Id);Number r.Start;Number r.Size;Number(int64 r.Pid);Text r.Owner;Number(int64(int r.Ring))]) |> Array.toList |> table ["region";"start";"size";"pid";"owner";"ring"]
                | MemoryAllocate(pid,size) ->
                    let p = processes.GetAuthorized(token,pid,"memory.allocate")
                    let r = memory.Allocate(token,pid,p.Owner,p.Ring,p.MemoryLimit,size)
                    table ["region";"size"] [[Number(int64 r.Id);Number r.Size]]
                | MemoryRelease id -> memory.Release(token,id); ok()
                | FileList path ->
                    fsReady()
                    files.List(token,path) |> Array.map (fun f -> [Text f.Path;Text(if f.Directory then "dir" else "file");Text f.Owner;Flag f.PublicRead;Number(int64 f.Content.Length);Text(f.Modified.ToString("O"))]) |> Array.toList |> table ["path";"type";"owner";"public.read";"length";"modified"]
                | FileRead path -> fsReady(); files.Read(token,path) |> Table.message
                | FileWrite(path,content) -> fsReady(); files.Write(token,path,content); ok()
                | DirectoryCreate path -> fsReady(); files.Mkdir(token,path); ok()
                | FileDelete path -> fsReady(); files.Delete(token,path); ok()
                | FileMove(source,target) -> fsReady(); files.Move(token,source,target); ok()
                | FilePermission(path,permission) -> fsReady(); files.Permission(token,path,permission); ok()
                | ServiceList ->
                    services.List token |> Array.map (fun s -> [Text s.Id;Text(if s.Running then "Running" else "Stopped");Text(String.concat "," s.Dependencies);Number(int64 s.Ring);Text(String.concat "," s.Capabilities);Flag s.AutoStart;Number s.Ticks;Text s.LastError]) |> Array.toList |> table ["id";"state";"dependencies";"ring";"capabilities";"autostart";"ticks";"error"]
                | ServiceStart name -> services.Start(token,name); ok()
                | ServiceStop name -> services.Stop(token,name); ok()
                | ServiceRestart name -> services.Restart(token,name); ok()
                | DeviceList | DeviceInfo _ ->
                    guard token "device.query" "devices" |> ignore
                    let rows = ["disk0","WVFS snapshot","Mounted";"mem0","Logical regions","Online";"timer0","Cooperative clock",(if services.IsRunning "clock" then "Online" else "Stopped")]
                    let rows = match request with DeviceInfo name -> rows |> List.filter (fun (id,_,_) -> id = name) | _ -> rows
                    if rows.IsEmpty then Errors.fail "WQX-DEV-001" "Dispositivo virtual inexistente."
                    rows |> List.map (fun (id,kind,status) -> [Text id;Text kind;Text status]) |> table ["id";"kind";"state"]
                | IdentityQuery ->
                    guard token "identity.query" ctx.User |> ignore
                    table ["user";"ring";"capabilities"] [[Text ctx.User;Number(int64(int ctx.Ring));Text(String.concat "," ctx.Capabilities)]]
                | IdentityList ->
                    services.Require "identity"
                    authority.Users token |> Array.map (fun u -> [Text(string u.Id);Text u.Name;Text(String.concat "," u.Roles);Text(String.concat "," u.Groups)]) |> Array.toList |> table ["id";"name";"roles";"groups"]
                | IdentityCreate(name,password) -> services.Require "identity"; authority.Create(token,name,password); files.EnsureHome name; ok()
                | IdentityGrant(name,role) -> services.Require "identity"; authority.ChangeRole(token,name,role,true); ok()
                | IdentityRevoke(name,role) -> services.Require "identity"; authority.ChangeRole(token,name,role,false); ok()
                | LogRead audit ->
                    guard token (if audit then "security.audit.read" else "log.read") "journal" |> ignore
                    journal.Entries |> Array.filter (fun e -> (e.Kind = "Authorization" || e.Kind = "SecurityViolation" || e.Kind.StartsWith "Identity") = audit) |> Array.map (fun e -> [Number e.Sequence;Text(e.Time.ToString("O"));Text e.Level;Text e.Kind;Text e.Actor;Text e.Target;Text e.Message]) |> Array.toList |> table ["sequence";"time";"level";"kind";"actor";"target";"message"])
            |> fun result ->
                match result with
                | Result.Error e -> journal.Write((if e.Code = "WQX-SEC-001" then Warning else Error),(if e.Code = "WQX-SEC-001" then "SecurityViolation" else "SyscallFailed"),actor,request.GetType().Name,e.Code)
                | Ok _ -> ()
                result
        with ex -> journal.Write(Error,"UnhandledException","system","syscall",ex.GetType().Name); Errors.protect(fun () -> raise ex)
    member this.Call(token,request) = serial(fun () -> this.Dispatch(token,request))
    interface IKernel with
        member this.Call(session,request) = this.Call(session,request)
        member this.State = this.State
    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                try serial(fun () -> if state = KernelState.Running then shutdown())
                finally
                    disposed <- true
                    inbox.CompleteAdding()
                    lifecycle.Run("HOST","Descargar hilo supervisor y cerrar cola de solicitudes",fun () ->
                        supervisorThread.Join()
                        inbox.Dispose())
