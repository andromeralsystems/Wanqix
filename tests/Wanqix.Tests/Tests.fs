namespace Wanqix.Tests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Wanqix.Core
open Wanqix.Security
open Wanqix.Memory
open Wanqix.Storage
open Wanqix.Services
open Wanqix.KSL
open Wanqix.Terminal

module Tests =
    let mutable checks = 0
    let check name condition =
        checks <- checks + 1
        if not condition then failwith ("FAIL: " + name)
    let equal name expected actual = check (name + sprintf " (expected %A, actual %A)" expected actual) (expected = actual)
    let ok = function Ok v -> v | Result.Error e -> failwith(sprintf "%s: %s (%A)" e.Code e.Message e.Position)
    let code expected result =
        match result with Result.Error e -> equal "error code" expected e.Code | Ok _ -> check ("Expected error " + expected) false
    let text column (t: Table) = Cell.text t.Rows.Head[column]
    let number column (t: Table) = match t.Rows.Head[column] with Number n -> n | _ -> failwith "Expected typed number"
    let password = "Wanqix-test-password-2026!"
    type MemoryStore() =
        let mutable json: string option = None
        member _.Json = json.Value
        interface IStateStore with
            member _.Load<'T>(validate: 'T -> unit) =
                json |> Option.map (fun s -> let value = JsonSerializer.Deserialize<'T> s in validate value; value),false
            member _.Save<'T>(value: 'T) = json <- Some(JsonSerializer.Serialize value)
    type FailingStore() =
        let inner = MemoryStore() :> IStateStore
        member val FailWrites = false with get,set
        interface IStateStore with
            member _.Load<'T>(validate: 'T -> unit) = inner.Load<'T> validate
            member this.Save<'T>(value: 'T) =
                if this.FailWrites then raise(IOException "Injected disk failure")
                inner.Save value
    let boot store = Kernel.Boot(store,clock = ManualClock(DateTimeOffset.Parse "2026-01-01T00:00:00Z"),initialRootPassword = password)
    let session (k: Kernel) token = ShellSession(k,token,(fun n p -> k.Login(n,p)),k.Guest,k.Logout)
    let execute (s: ShellSession) source = s.Execute source |> List.map ok

    let lifecycleAndProcesses () =
        let store = MemoryStore()
        use k = boot store
        equal "boot state" KernelState.Running k.State
        let guest = k.Guest()
        let admin = k.Login("root",password) |> ok
        let call token request = k.Call(token,request)
        equal "root ring 2" "2" (call admin IdentityQuery |> ok |> text "ring")
        equal "guest ring 3" "3" (call guest IdentityQuery |> ok |> text "ring")
        code "WQX-SEC-001" (call (Guid.NewGuid()) KernelInspect)
        code "WQX-SEC-001" (k.Login("root","bad"))
        let supervisor = call admin ProcessList |> ok
        equal "supervisor ring 0" "0" (text "ring" supervisor)
        let before = supervisor.Rows
        code "WQX-SEC-001" (call admin (ProcessControl(100,End)))
        code "WQX-SEC-001" (call guest (ProcessControl(100,End)))
        equal "protected process intact" before (call admin ProcessList |> ok).Rows
        code "WQX-PROC-002" (call guest (ProcessControl(99999,End)))
        let pid = call guest (ProcessCreate("worker",2,4194304L,20)) |> ok |> number "pid" |> int
        equal "first PID" 101 pid
        let pid2 = call guest (ProcessCreate("other",0,1048576L,3)) |> ok |> number "pid" |> int
        equal "unique monotonic PID" 102 pid2
        equal "created to ready" "Ready" (call guest ProcessList |> ok |> text "state")
        let region = call guest (MemoryAllocate(pid,1048576L)) |> ok |> number "region" |> int
        let memoryBefore = (call admin MemoryMap |> ok).Rows
        code "WQX-SEC-001" (call guest (MemoryRelease 0))
        code "WQX-SEC-001" (call admin (MemoryRelease 0))
        equal "ring0 memory unchanged" memoryBefore (call admin MemoryMap |> ok).Rows
        code "WQX-SEC-001" (call guest (MemoryAllocate(100,1L)))
        code "WQX-MEM-003" (call guest (MemoryAllocate(pid,0L)))
        code "WQX-MEM-003" (call guest (MemoryAllocate(pid,4194304L)))
        equal "invalid allocations unchanged" memoryBefore (call admin MemoryMap |> ok).Rows
        call guest (ProcessControl(pid,Pause)) |> ok |> ignore
        for _ in 1..5 do k.Tick()
        let paused = call guest ProcessList |> ok |> fun t -> t.Rows |> List.find (fun r -> Cell.text r["pid"] = string pid)
        equal "paused execution" (Number 0L) paused["runtime"]
        equal "paused state" (Text "Suspended") paused["state"]
        call guest (ProcessControl(pid,Resume)) |> ok |> ignore
        for _ in 1..10 do k.Tick()
        let finished = call guest ProcessList |> ok |> fun t -> t.Rows |> List.find (fun r -> Cell.text r["pid"] = string pid)
        equal "task completes real work" (Number 20L) finished["runtime"]
        equal "task terminated" (Text "Terminated") finished["state"]
        equal "task memory reclaimed" [] (call guest MemoryMap |> ok).Rows
        code "WQX-MEM-003" (call guest (MemoryRelease region))
        code "WQX-PROC-003" (call guest (ProcessControl(pid,Resume)))
        let pid3 = call guest (ProcessCreate("cancel",0,1048576L,100)) |> ok |> number "pid" |> int
        call guest (MemoryAllocate(pid3,100L)) |> ok |> ignore
        call guest (ProcessControl(pid3,End)) |> ok |> ignore
        equal "cancel releases memory" [] (call guest MemoryMap |> ok).Rows
        code "WQX-ARG-001" (call guest (ProcessCreate("invalid",10,1L,1)))
        code "WQX-ARG-001" (call guest (ProcessCreate("invalid",1,1L,0)))
        code "WQX-SEC-001" (call guest KernelShutdown)
        equal "denied shutdown leaves running" KernelState.Running k.State
        call admin KernelShutdown |> ok |> ignore
        equal "stopped" KernelState.Stopped k.State
        code "WQX-KRN-005" (call admin KernelInspect)
        check "no plaintext password" (not (store.Json.Contains password))

    let identities () =
        use k = boot (MemoryStore())
        let admin = k.Login("root",password) |> ok
        let guest = k.Guest()
        let call t r = k.Call(t,r)
        let before = call admin IdentityList |> ok
        code "WQX-SEC-001" (call guest (IdentityCreate("evil",password)))
        equal "identities unchanged on deny" before (call admin IdentityList |> ok)
        code "WQX-SEC-001" (call guest (IdentityGrant("guest","admin")))
        equal "guest still ring3" "3" (call guest IdentityQuery |> ok |> text "ring")
        code "WQX-ARG-001" (call admin (IdentityCreate("../evil",password)))
        call admin (IdentityCreate("alice",password)) |> ok |> ignore
        call admin (IdentityCreate("bob",password)) |> ok |> ignore
        let alice = k.Login("alice",password) |> ok
        let bob = k.Login("bob",password) |> ok
        let pid = call alice (ProcessCreate("alice-job",1,1048576L,100)) |> ok |> number "pid" |> int
        call alice (MemoryAllocate(pid,100L)) |> ok |> ignore
        let before = call admin ProcessList |> ok
        code "WQX-SEC-001" (call bob (ProcessControl(pid,End)))
        equal "cross-user process intact" before (call admin ProcessList |> ok)
        let region = call alice MemoryMap |> ok |> number "region" |> int
        code "WQX-SEC-001" (call bob (MemoryRelease region))
        equal "cross-user region intact" 1 (call alice MemoryMap |> ok).Rows.Length
        call admin (IdentityGrant("alice","admin")) |> ok |> ignore
        code "WQX-SEC-001" (call alice (ServiceStop "metrics"))
        let aliceAdmin = k.Login("alice",password) |> ok
        equal "new admin session ring2" "2" (call aliceAdmin IdentityQuery |> ok |> text "ring")
        call aliceAdmin (ServiceStop "metrics") |> ok |> ignore
        call admin (IdentityRevoke("alice","admin")) |> ok |> ignore
        code "WQX-SEC-001" (call aliceAdmin (ServiceStart "metrics"))
        let metrics = call admin ServiceList |> ok |> fun t -> t.Rows |> List.find (fun r -> r["id"] = Text "metrics")
        equal "revoked token cannot mutate service" (Text "Stopped") metrics["state"]
        k.Logout bob
        code "WQX-SEC-001" (call bob KernelInspect)
        let audit = call admin (LogRead true) |> ok
        check "audit contains denials" (audit.Rows |> List.exists (fun row -> row["kind"] = Text "SecurityViolation"))
        code "WQX-SEC-001" (call guest (LogRead true))
        code "WQX-SEC-001" (call admin (IdentityRevoke("root","admin")))

    let filesAndPersistence () =
        let store = MemoryStore()
        let k = boot store
        let admin = k.Login("root",password) |> ok
        let guest = k.Guest()
        let call token request = k.Call(token,request)
        call admin (FileWrite("/users/root/private","protected")) |> ok |> ignore
        code "WQX-SEC-001" (call guest (FileRead "/users/root/private"))
        code "WQX-SEC-001" (call guest (FileWrite("/users/root/private","evil")))
        equal "private unchanged" "protected" (call admin (FileRead "/users/root/private") |> ok |> text "result")
        for path in ["/../../outside";"C:\\Windows\\system.ini";"/temp/../../../secret";"/temp/evil\u0000"] do
            code "WQX-FS-004" (call guest (FileWrite(path,"evil")))
        call guest (DirectoryCreate "/users/guest/projects") |> ok |> ignore
        call guest (FileWrite("/users/guest/projects/a.txt","hello\nworld")) |> ok |> ignore
        call guest (FileMove("/users/guest/projects","/users/guest/renamed")) |> ok |> ignore
        equal "subtree moved" "hello\nworld" (call guest (FileRead "/users/guest/renamed/a.txt") |> ok |> text "result")
        code "WQX-FS-006" (call guest (FileMove("/users/guest/renamed","/users/guest/renamed/sub")))
        code "WQX-SEC-001" (call guest (FileMove("/users/guest/renamed/a.txt","/system/config/evil")))
        equal "denied move retains source" "hello\nworld" (call guest (FileRead "/users/guest/renamed/a.txt") |> ok |> text "result")
        code "WQX-FS-006" (call guest (FileDelete "/users/guest/renamed"))
        code "WQX-SEC-001" (call admin (FileDelete "/system"))
        call guest (FileWrite("/temp/public.txt","shared")) |> ok |> ignore
        call guest (FilePermission("/temp/public.txt",true)) |> ok |> ignore
        call admin (IdentityCreate("reader",password)) |> ok |> ignore
        let reader = k.Login("reader",password) |> ok
        equal "public file readable" "shared" (call reader (FileRead "/temp/public.txt") |> ok |> text "result")
        code "WQX-SEC-001" (call reader (FileWrite("/temp/public.txt","overwrite")))
        call admin (ConfigSet("color","false")) |> ok |> ignore
        let config = k.Config
        code "WQX-CFG-001" (call admin (ConfigSet("memory.capacity","1")))
        equal "invalid config unchanged" config k.Config
        code "WQX-SEC-001" (call guest (ConfigSet("color","true")))
        equal "denied config unchanged" config k.Config
        k.Remember "sys.info"
        k.Remember ("usr.login root " + password)
        k.Shutdown()
        (k :> IDisposable).Dispose()
        use next = boot store
        let admin2 = next.Login("root",password) |> ok
        equal "persistent file" "hello\nworld" (next.Call(admin2,FileRead "/users/guest/renamed/a.txt") |> ok |> text "result")
        equal "persistent config" false next.Config.Color
        equal "persistent identity" 3 (next.Call(admin2,IdentityList) |> ok).Rows.Length
        equal "persistent boot counter" 2L (next.Call(admin2,KernelInspect) |> ok |> number "boot")
        equal "persistent history without password" [|"sys.info"|] next.History
        code "WQX-SEC-001" (next.Call(admin,KernelInspect))

    let languageAndShell () =
        use k = boot (MemoryStore())
        let admin = k.Login("root",password) |> ok
        let s = session k admin
        equal "quoted tokens" ["/temp/a b";"one | two && three > four"] ((Parser.parse "fs.write \"/temp/a b\" \"one | two && three > four\"" |> ok).Head.Commands.Head.Arguments)
        execute s "fs.write \"/temp/a b\" \"one | two && three > four\"; fs.read \"/temp/a b\"" |> fun ts -> equal "quoted execution" "one | two && three > four" (text "result" ts[1])
        let structured = "@process :: spawn { identity: \"worker.alpha\"; priority: 4; memory.limit: 128MiB; steps: 20; }"
        let quick = "proc.spawn worker.alpha --priority 4 --memory 128MiB --steps 20"
        let registry = CommandRegistry()
        let resolve source = Parser.parse source |> ok |> _.Head.Commands.Head |> registry.Resolve
        equal "shared IR" (resolve quick) (resolve structured)
        let p = execute s structured |> List.head |> number "pid" |> int
        execute s (sprintf "mem.alloc %d 1MiB" p) |> ignore
        equal "structured memory" 2 (execute s "@memory :: inspect { region: all; format: detailed; }" |> List.head).Rows.Length
        let filtered = execute s "proc | where name worker.alpha | sort pid --desc | select pid name | take 1" |> List.head
        equal "typed pipeline columns" ["pid";"name"] filtered.Columns
        equal "typed pipeline numeric cell" (Number(int64 p)) filtered.Rows.Head["pid"]
        execute s "proc | select pid name > /temp/processes.tsv" |> ignore
        check "redirection persisted" ((k.Call(admin,FileRead "/temp/processes.tsv") |> ok |> text "result").Contains "worker.alpha")
        execute s "set label job.beta; alias jobs proc.list; proc.spawn $label --steps 5; jobs | where name job.beta" |> fun results -> equal "variables and alias" "job.beta" (text "name" results[3])
        execute s "fs.go /temp; fs.new nested; fs.go nested; fs.write file.txt payload; fs.go ..; fs.read nested/file.txt" |> fun results -> equal "cwd and relative paths" "payload" (text "result" results[5])
        equal "cwd canonical" "/temp" s.Cwd
        code "WQX-ARG-001" (s.Execute "fs.go nested/file.txt" |> List.head)
        equal "failed cd unchanged" "/temp" s.Cwd
        execute s "fs.write /temp/script.ksl \"fs.write /temp/from-script yes; sys.info\"; run /temp/script.ksl" |> ignore
        equal "WVFS script effects" "yes" (k.Call(admin,FileRead "/temp/from-script") |> ok |> text "result")
        s.Execute "fs.read /missing && fs.write /temp/skipped bad; fs.write /temp/continued good" |> ignore
        code "WQX-FS-005" (k.Call(admin,FileRead "/temp/skipped"))
        equal "semicolon continues after error" "good" (k.Call(admin,FileRead "/temp/continued") |> ok |> text "result")
        for malformed in ["@process :: spawn { identity: x;";"fs.write x \"unterminated";"sys &&";"sys |";"sys >";"@kernel :: inspect { unexpected: true; }";"@process :: spawn { identity: x; ring: 0; }";"@process :: spawn { identity: x; capabilities: admin; }";"proc.spawn x --priority 1 --priority 2"] do
            check ("reject malformed " + malformed) (s.Execute malformed |> List.exists Result.isError)
        let guestSession = session k (k.Guest())
        let before = k.Call(admin,ProcessList) |> ok
        check "script cannot forge ring" (guestSession.Execute "@process :: spawn { identity: root; ring: 0; }" |> List.exists Result.isError)
        equal "malformed escalation preserves process table" before (k.Call(admin,ProcessList) |> ok)
        code "WQX-SEC-001" (guestSession.Execute "svc.stop metrics" |> List.head)
        code "WQX-ARG-001" (s.Execute "proc | select nonexisting" |> List.head)
        code "WQX-ARG-001" (s.Execute "proc | mem.map" |> List.head)
        execute s "alias a b; alias b a" |> ignore
        code "WQX-ARG-001" (s.Execute "a" |> List.head)
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        code "WQX-KRN-006" (s.Execute("sys",cancellation = cancellation.Token) |> List.head)
        let injected = "\u001b]52;c;stolen\u0007"
        check "terminal control escaping" (not ((Render.safe injected).Contains '\u001b'))
        execute s "usr.logout" |> ignore
        equal "logout changes prompt identity" "guest" s.User
        equal "logout changes home" "/users/guest" s.Cwd
        code "WQX-SEC-001" (k.Call(admin,KernelInspect))

    let serviceDependencies () =
        use k = boot (MemoryStore())
        let a = k.Login("root",password) |> ok
        let call r = k.Call(a,r)
        let before = call ServiceList |> ok
        code "WQX-SVC-002" (call (ServiceStop "logger"))
        equal "dependency rejection unchanged" before (call ServiceList |> ok)
        call (ServiceStop "identity") |> ok |> ignore
        call (ServiceStop "metrics") |> ok |> ignore
        call (ServiceStop "wvfs") |> ok |> ignore
        code "WQX-SVC-003" (call (FileList "/"))
        code "WQX-SVC-003" (k.Login("root",password))
        call (ServiceStart "identity") |> ok |> ignore
        call (FileList "/") |> ok |> ignore
        call (ServiceRestart "metrics") |> ok |> ignore
        for _ in 1..5 do k.Tick()
        let metrics = call ServiceList |> ok |> fun t -> t.Rows |> List.find (fun r -> r["id"] = Text "metrics")
        equal "service executes pulses" (Number 5L) metrics["ticks"]
        code "WQX-SVC-001" (call (ServiceStart "not-found"))

    let atomicRecovery () =
        let root = Path.Combine(Path.GetTempPath(),"Wanqix.Tests." + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory root |> ignore
        try
            use backing = new AtomicStateStore(root)
            let store = backing :> IStateStore
            code "WQX-STO-002" (Errors.protect(fun () -> new AtomicStateStore(root)))
            let k = boot store
            let a = k.Login("root",password) |> ok
            k.Call(a,FileWrite("/users/root/backup-proof","saved")) |> ok |> ignore
            k.Checkpoint()
            k.Shutdown()
            (k :> IDisposable).Dispose()
            File.WriteAllText(Path.Combine(root,"state.v1.json"),"{ damaged")
            use recovered = boot store
            let a = recovered.Login("root",password) |> ok
            equal "backup recovers file" "saved" (recovered.Call(a,FileRead "/users/root/backup-proof") |> ok |> text "result")
            let journal = recovered.Call(a,LogRead false) |> ok
            check "recovery recorded" (journal.Rows |> List.exists (fun r -> r["kind"] = Text "StateRecovered"))
            recovered.Checkpoint()
            check "valid backup retained" ((File.ReadAllText(Path.Combine(root,"state.v1.backup.json"))).StartsWith "{")
            recovered.Shutdown()
            File.WriteAllText(Path.Combine(root,"state.v1.json"),"broken")
            File.WriteAllText(Path.Combine(root,"state.v1.backup.json"),"broken")
            code "WQX-STO-001" (Errors.protect(fun () -> boot store))
        finally
            // Only remove this test's GUID-named temp directory.
            if Path.GetDirectoryName(Path.GetFullPath root) = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(root).StartsWith "Wanqix.Tests." then Directory.Delete(root,true)

    let concurrentCalls () =
        use k = boot (MemoryStore())
        let guest = k.Guest()
        let tasks = [| for n in 1..32 -> Task.Run(fun () -> k.Call(guest,ProcessCreate("parallel-" + string n,1,1024L,10)) |> ok |> number "pid") |]
        Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))
        equal "concurrent PID uniqueness" 32 (tasks |> Array.map _.Result |> Set.ofArray |> Set.count)
        equal "concurrent process count" 32 (k.Call(guest,ProcessList) |> ok).Rows.Length

    let authorityAndFragmentation () =
        let clock = ManualClock(DateTimeOffset.Parse "2026-01-01T00:00:00Z")
        let journal = EventJournal(clock)
        let users = [|Authority.CreateUser("root",password,[|"admin"|]);Authority.CreateUser("guest",password,[|"user"|])|]
        let authority = Authority(journal,users,Authority.DefaultRoles,clock)
        let service = authority.RegisterSystemService("metrics",[|"kernel.inspect"|])
        equal "internal service ring1" Ring.System (authority.Context service).Ring
        authority.Authorize service "kernel.inspect" "kernel" |> ignore
        code "WQX-SEC-001" (Errors.protect(fun () -> authority.Authorize service "identity.manage" "users"))
        let root = authority.Login("root",password)
        code "WQX-SEC-001" (Errors.protect(fun () -> authority.Authorize root "kernel.supervisor" "kernel"))
        let guest = authority.Guest()
        let guard : Guard = fun t op target -> authority.Authorize t op target
        let memory = MemoryManager(1048576L,guard,journal)
        let allocate n = memory.Allocate(guest,101,"guest",Ring.User,1048576L,n)
        let a,b,c = allocate 300000L,allocate 300000L,allocate 300000L
        memory.Release(guest,b.Id)
        equal "fragmentation largest hole" 300000L memory.LargestFree
        check "fragmentation free total sufficient" (memory.Capacity - memory.Used > 350000L)
        let before = memory.Map guest
        code "WQX-MEM-003" (Errors.protect(fun () -> allocate 350000L))
        equal "fragmented rejection unchanged" before (memory.Map guest)
        memory.Release(guest,c.Id)
        let large = allocate 350000L
        equal "first-fit reuses hole" (a.Start + a.Size) large.Start
        code "WQX-MEM-003" (Errors.protect(fun () -> memory.Release(guest,9999)))
        code "WQX-SEC-001" (Errors.protect(fun () -> memory.Allocate(guest,100,"system",Ring.Supervisor,100L,1L)))
        for _ in 1..5 do code "WQX-SEC-001" (Errors.protect(fun () -> authority.Login("root","wrong")))
        code "WQX-SEC-003" (Errors.protect(fun () -> authority.Login("root",password)))
        clock.Advance(TimeSpan.FromMinutes 1.)
        let token = authority.Login("root",password)
        equal "login after cooldown" Ring.Administrative (authority.Context token).Ring
        clock.Advance(TimeSpan.FromHours 12.)
        code "WQX-SEC-001" (Errors.protect(fun () -> authority.Context token))
        equal "integer size units" 1073741824L (Numbers.size "1GiB")
        code "WQX-ARG-001" (Errors.protect(fun () -> Numbers.size "9223372036854775807GiB"))

    let parserFuzz () =
        let random = Random 271828
        let alphabet = "abc012 @:{};|>&#\"'\\\n\t$.-/"
        for _ in 1..200 do
            let source = String(Array.init (random.Next(0,120)) (fun _ -> alphabet[random.Next alphabet.Length]))
            let result = Parser.parse source
            match result with
            | Ok _ -> check "fuzz parsed deterministically" (result = Parser.parse source)
            | Result.Error e -> check "malformed input returns positioned stable error" (e.Code = "WQX-KSL-001" && e.Position.IsSome)

    let controlledFailure () =
        let store = FailingStore()
        use k = boot store
        let root = k.Login("root",password) |> ok
        store.FailWrites <- true
        code "WQX-KRN-500" (k.Call(root,KernelShutdown))
        equal "persistence failure state" KernelState.Failed k.State
        code "WQX-KRN-005" (k.Call(root,KernelInspect))
        let directory = Path.Combine(Path.GetTempPath(),"Wanqix.Tests." + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory directory |> ignore
        try
            File.WriteAllText(Path.Combine(directory,"state.v1.backup.json"),"corrupt-only-backup")
            use backing = new AtomicStateStore(directory)
            code "WQX-STO-001" (Errors.protect(fun () -> boot backing))
        finally
            if Path.GetDirectoryName(Path.GetFullPath directory) = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(directory).StartsWith "Wanqix.Tests." then Directory.Delete(directory,true)

    let lifecycleOutput () =
        let events = ResizeArray<LifecycleEvent>()
        let store = MemoryStore()
        use k = Kernel.Boot(store,initialRootPassword = password,lifecycle = LifecycleReporter(observer = events.Add))
        check "boot reports actual load" (events |> Seq.exists (fun e -> e.Operation.StartsWith "Leer y validar" && e.Status = Completed))
        let started = events |> Seq.findIndex (fun e -> e.Operation = "Iniciar hilo supervisor y habilitar WSCI" && e.Status = Completed)
        let ready = events |> Seq.findIndex (fun e -> e.Operation = "Kernel preparado")
        check "ready reported after supervisor start" (started < ready && k.State = KernelState.Running)
        let serviceStarts = events |> Seq.filter (fun e -> e.Phase = "SVC" && e.Status = Completed) |> Seq.map _.Operation |> Seq.toList
        equal "real dependency start order" ["Iniciar servicio logger (Ring 1)";"Iniciar servicio clock (Ring 1)";"Iniciar servicio wvfs (Ring 1)";"Iniciar servicio identity (Ring 1)";"Iniciar servicio metrics (Ring 1)"] serviceStarts
        let guest = k.Guest()
        let before = events.Count
        code "WQX-SEC-001" (k.Call(guest,KernelShutdown))
        equal "denied shutdown emits no lifecycle actions" before events.Count
        let root = k.Login("root",password) |> ok
        k.Call(root,ProcessCreate("active",0,1024L,100)) |> ok |> ignore
        k.Call(root,KernelReboot) |> ok |> ignore
        check "reboot reports actual process terminated" (events |> Seq.exists (fun e -> e.Phase = "REBOOT" && e.Status = Completed && e.Operation.Contains "PID 101"))
        let saved = JsonSerializer.Deserialize<Snapshot> store.Json
        check "shutdown snapshot really stopped services" (saved.Services |> Array.forall (fun s -> not s.Running))
        let savedAt = events |> Seq.findIndex (fun e -> e.Phase = "REBOOT" && e.Operation = "Guardar snapshot atómico del sistema" && e.Status = Completed)
        let stoppedAt = events |> Seq.findIndex (fun e -> e.Phase = "REBOOT" && e.Operation = "Kernel detenido")
        check "stopped reported after successful save" (savedAt < stoppedAt && k.State = KernelState.Stopped)
        check "elapsed times monotonic" (events |> Seq.pairwise |> Seq.forall (fun (a,b) -> a.Elapsed <= b.Elapsed))
        check "no credentials in lifecycle output" (events |> Seq.forall (fun e -> not ((e.Operation + e.Detail).Contains password)))
        let failures = ResizeArray<LifecycleEvent>()
        let failing = FailingStore()
        use failed = Kernel.Boot(failing,initialRootPassword = password,lifecycle = LifecycleReporter(observer = failures.Add))
        let admin = failed.Login("root",password) |> ok
        failing.FailWrites <- true
        code "WQX-KRN-500" (failed.Call(admin,KernelShutdown))
        check "save failure reported as failed" (failures |> Seq.exists (fun e -> e.Phase = "STOP" && e.Operation = "Guardar snapshot atómico del sistema" && e.Status = StepFailed && e.Detail = "IOException"))
        check "save failure never reported as completed" (failures |> Seq.forall (fun e -> e.Phase <> "STOP" || (e.Operation <> "Kernel detenido" && not (e.Operation = "Guardar snapshot atómico del sistema" && e.Status = Completed))))
        equal "failed shutdown still failed" KernelState.Failed failed.State
        use silent = Kernel.Boot(MemoryStore(),initialRootPassword = password,lifecycle = LifecycleReporter(observer = (fun _ -> failwith "Broken presentation")))
        equal "observer failure cannot break kernel" KernelState.Running silent.State

    [<EntryPoint>]
    let main _ =
        try
            for name,test in ["Lifecycle/process/memory",lifecycleAndProcesses;"Identity/security",identities;"Files/persistence",filesAndPersistence;"KSL/shell",languageAndShell;"Services",serviceDependencies;"Atomic recovery",atomicRecovery;"Concurrency",concurrentCalls;"Ring 1/expiry/fragmentation",authorityAndFragmentation;"Parser robustness",parserFuzz;"Controlled persistence failure",controlledFailure;"Truthful lifecycle output",lifecycleOutput] do
                test()
                printfn "PASS %s" name
            printfn "%d assertions passed." checks
            0
        with ex -> eprintfn "%s\n%s" ex.Message ex.StackTrace; 1
