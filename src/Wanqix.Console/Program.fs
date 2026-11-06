namespace Wanqix.ConsoleApp

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Wanqix.Core
open Wanqix.Storage
open Wanqix.Services
open Wanqix.KSL
open Wanqix.Terminal
open Wanqix.Host.Windows

module Program =
    type Options = { Data: string; Command: string option; Script: string option; Admin: bool; Deterministic: bool; NoColor: bool }
    let parse args =
        let rec loop options = function
            | [] -> options
            | "--data"::path::rest -> loop { options with Data = Path.GetFullPath path } rest
            | "--command"::command::rest -> loop { options with Command = Some command } rest
            | "--script"::path::rest -> loop { options with Script = Some path } rest
            | "--admin"::rest -> loop { options with Admin = true } rest
            | "--deterministic"::rest -> loop { options with Deterministic = true } rest
            | "--no-color"::rest -> loop { options with NoColor = true } rest
            | _ -> Errors.invalid "Opciones: --data path --command KSL --script path.ksl --admin --deterministic --no-color."
        let options = loop { Data = WindowsHost.defaultDataDirectory(); Command = None; Script = None; Admin = false; Deterministic = false; NoColor = false } (Array.toList args)
        if options.Command.IsSome && options.Script.IsSome then Errors.invalid "Use --command o --script."
        options
    let unwrap = function Ok value -> value | Result.Error e -> raise(WqxException e)
    let quote (s: string) = "\"" + s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r") + "\""
    [<EntryPoint>]
    let main args =
        WindowsHost.initialize()
        try
            if args |> Array.contains "--help" then
                printfn "Wanqix 1.0 — kernel simulado F#/.NET 10\n--data PATH         almacenamiento aislado\n--command KSL       ejecución no interactiva\n--script PATH.ksl   lee únicamente el script anfitrión indicado\n--admin             autentica root (WANQIX_ROOT_PASSWORD o entrada oculta)\n--deterministic     avanza solo con sys.tick\n--no-color          salida sin colores\nPrimera ejecución: establezca WANQIX_ROOT_PASSWORD (12+ caracteres) o ingrésela en consola.\nLa sesión normal comienza como guest. Escriba help para consultar KSL."
                0
            else
                let options = parse args
                let lifecycle = LifecycleReporter(observer = Render.lifecycle (not options.NoColor))
                lifecycle.Notice("BOOT","Runtime del anfitrión inicializado",System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
                let interactive = options.Command.IsNone && options.Script.IsNone && not Console.IsInputRedirected
                let editor = LineEditor((fun () -> (CommandRegistry()).Definitions |> Array.map _.Name),[||])
                let mutable rootPassword = Environment.GetEnvironmentVariable "WANQIX_ROOT_PASSWORD"
                let newState = not (File.Exists(Path.Combine(options.Data,"state.v1.json")) || File.Exists(Path.Combine(options.Data,"state.v1.backup.json")))
                if newState && String.IsNullOrEmpty rootPassword then
                    if interactive then
                        printfn "Primer arranque: configure la contraseña virtual de root (mínimo 12 caracteres)."
                        rootPassword <- editor.ReadSecret "Contraseña: "
                        let repeat = editor.ReadSecret "Repetir: "
                        if rootPassword <> repeat then Errors.invalid "Las contraseñas no coinciden."
                    else Errors.fail "WQX-SEC-004" "Primera ejecución no interactiva: defina WANQIX_ROOT_PASSWORD con 12+ caracteres."
                let mutable exitCode = 0
                let mutable restart = true
                while restart do
                    restart <- false
                    let backing = lifecycle.Run("BOOT","Abrir backend aislado y adquirir exclusión de instancia",fun () -> new AtomicStateStore(options.Data))
                    use storageLifetime = { new IDisposable with member _.Dispose() = lifecycle.Run("HOST","Cerrar backend y liberar exclusión de instancia",fun () -> (backing :> IDisposable).Dispose()) }
                    let store = backing :> IStateStore
                    use kernel = Kernel.Boot(store,initialRootPassword = (if isNull rootPassword then "" else rootPassword),lifecycle = lifecycle)
                    if interactive then
                        printfn "Wanqix 1.0 · cuatro anillos virtuales · .NET 10 / F# 10"
                        printfn "Escriba help; Tab completa; ↑/↓ historial; Ctrl+D sale. usr.login root pide la contraseña sin eco."
                    let mutable token = lifecycle.Run("BOOT","Crear sesión virtual guest (Ring 3)",kernel.Guest)
                    if options.Admin then
                        if String.IsNullOrEmpty rootPassword then
                            if interactive then rootPassword <- editor.ReadSecret "Contraseña de root: "
                            else Errors.fail "WQX-SEC-004" "--admin requiere WANQIX_ROOT_PASSWORD en modo no interactivo."
                        kernel.Logout token
                        token <- lifecycle.Run("BOOT","Autenticar sesión administrativa root (Ring 2)",fun () -> kernel.Login("root",rootPassword) |> unwrap)
                    let session = lifecycle.Run("BOOT","Preparar intérprete KSL y contexto de sesión",fun () -> ShellSession(kernel,token,(fun name password -> kernel.Login(name,password)),kernel.Guest,kernel.Logout))
                    let complete () =
                        let files = kernel.Call(session.Token,FileList session.Cwd)
                        let paths = match files with Ok t -> t.Rows |> List.map (fun row -> let p = Cell.text row["path"] in p.Substring(p.LastIndexOf('/') + 1)) |> List.toArray | _ -> [||]
                        Array.append (session.Registry.Definitions |> Array.map _.Name) paths
                    let editor = lifecycle.Run("BOOT","Preparar editor de línea, historial y autocompletado",fun () -> LineEditor(complete,kernel.History))
                    use lifetime = new CancellationTokenSource()
                    let mutable activeCommand: CancellationTokenSource option = None
                    let cancelHandler = ConsoleCancelEventHandler(fun _ e ->
                        e.Cancel <- true
                        match activeCommand with Some c -> c.Cancel() | None -> lifetime.Cancel())
                    Console.CancelKeyPress.AddHandler cancelHandler
                    let worker : Task =
                        if options.Deterministic then Task.CompletedTask
                        else task {
                            try
                                while not lifetime.IsCancellationRequested && kernel.State = KernelState.Running do
                                    do! Task.Delay(kernel.Config.TickMilliseconds,lifetime.Token)
                                    kernel.Tick()
                            with :? OperationCanceledException -> () } :> Task
                    lifecycle.Notice("BOOT","Modo de ejecución",if options.Deterministic then "Reloj automático desactivado; avance mediante sys.tick." else sprintf "Reloj cooperativo automático activo (%d ms)." kernel.Config.TickMilliseconds)
                    let execute source =
                        use cancel = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                        activeCommand <- Some cancel
                        let results = session.Execute(source,cancellation = cancel.Token)
                        activeCommand <- None
                        if session.ClearRequested then
                            if not Console.IsOutputRedirected then Console.Clear()
                            session.ClearRequested <- false
                        for result in results do
                            match result with Ok table -> Render.table table | Result.Error error -> Render.error (not options.NoColor && kernel.Config.Color) error; exitCode <- 1
                        if kernel.State = KernelState.Running then kernel.Checkpoint()
                    try
                        match options.Command,options.Script with
                        | Some command,_ -> execute command
                        | _,Some path ->
                            if not (path.EndsWith(".ksl",StringComparison.OrdinalIgnoreCase)) || FileInfo(path).Length > 1048576L then Errors.invalid "Script anfitrión inválido o demasiado grande."
                            execute(File.ReadAllText path)
                        | _ ->
                            while kernel.State = KernelState.Running && not lifetime.IsCancellationRequested do
                                let home = "/users/" + session.User
                                let displayPath = if session.Cwd = home then "~" else session.Cwd
                                let prompt = "wanqix@" + session.User + " " + displayPath + " ❯ "
                                let line = editor.ReadLine(prompt,not options.NoColor && kernel.Config.Color)
                                if isNull line then lifetime.Cancel()
                                elif not (String.IsNullOrWhiteSpace line) then
                                    let mutable source = line
                                    // Collect structured blocks before parsing the entire script.
                                    let mutable more = true
                                    while more do
                                        let balance = try Lexer.tokenize source |> Array.sumBy (fun t -> match t.Kind with OpenBrace -> 1 | CloseBrace -> -1 | _ -> 0) with _ -> 0
                                        if balance > 0 then
                                            let next = editor.ReadLine("… ",not options.NoColor)
                                            if isNull next then more <- false else source <- source + "\n" + next
                                        else more <- false
                                    let mutable sensitive = false
                                    match Parser.parse source with
                                    | Ok [{ Commands = [{ Name = ("usr.login" | "usr.new") as name; Arguments = [user] }]; Destination = None }] ->
                                        sensitive <- true
                                        let secret = editor.ReadSecret "Contraseña virtual: "
                                        source <- name + " " + quote user + " " + quote secret
                                    | _ -> ()
                                    // Persist only commands proven not to contain credential/variable operations.
                                    let safeHistory =
                                        not sensitive &&
                                        (match Parser.parse source with
                                         | Ok pipelines -> pipelines |> List.forall (fun p -> p.Commands |> List.forall (fun c ->
                                             try match session.Registry.Resolve c with Local(Login _) | Local(SetVariable _) | Local(SetAlias _) | SystemCall(IdentityCreate _) -> false | _ -> not (source.Contains '$')
                                             with _ -> false))
                                         | _ -> false)
                                    if safeHistory then editor.Remember source; kernel.Remember source
                                    execute source
                    finally
                        lifecycle.Run("HOST","Cancelar tareas del anfitrión y esperar finalización",fun () ->
                            lifetime.Cancel()
                            worker.GetAwaiter().GetResult())
                        Console.CancelKeyPress.RemoveHandler cancelHandler
                        if kernel.State = KernelState.Running then kernel.Shutdown()
                    restart <- interactive && kernel.RebootRequested
                exitCode
        with
        | WqxException error -> Render.error false error; 1
        | ex -> eprintfn "WQX-HOST-001: %s" (Render.safe ex.Message); 1
