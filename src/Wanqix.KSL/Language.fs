namespace Wanqix.KSL

open System
open System.Text
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading
open Wanqix.Core

type TokenKind = Word of string | At | Colon | DoubleColon | OpenBrace | CloseBrace | Separator | Pipe | And | Redirect | Eof
type Token = { Kind: TokenKind; Position: int }
type Invocation = { Name: string; Arguments: string list; Fields: Map<string,string>; Position: int }
type Pipeline = { Commands: Invocation list; Destination: string option; Conditional: bool }
type Script = Pipeline list

module LanguageError =
    let fail position message = raise (WqxException { Code = "WQX-KSL-001"; Message = message; Position = Some position })

module Lexer =
    let tokenize (source: string) =
        if source.Length > 1048576 then LanguageError.fail 0 "Script demasiado grande."
        let tokens = ResizeArray<Token>()
        let mutable i = 0
        let emit start kind = tokens.Add { Kind = kind; Position = start }
        let special c = Char.IsWhiteSpace c || "@:{};|>&#\"'".Contains c
        while i < source.Length do
            let start = i
            match source[i] with
            | ' ' | '\t' | '\r' -> i <- i + 1
            | '\n' | ';' -> emit i Separator; i <- i + 1
            | '#' -> while i < source.Length && source[i] <> '\n' do i <- i + 1
            | '@' -> emit i At; i <- i + 1
            | '{' -> emit i OpenBrace; i <- i + 1
            | '}' -> emit i CloseBrace; i <- i + 1
            | ':' when i + 1 < source.Length && source[i + 1] = ':' -> emit i DoubleColon; i <- i + 2
            | ':' -> emit i Colon; i <- i + 1
            | '|' -> emit i Pipe; i <- i + 1
            | '>' -> emit i Redirect; i <- i + 1
            | '&' when i + 1 < source.Length && source[i + 1] = '&' -> emit i And; i <- i + 2
            | '&' -> LanguageError.fail i "Use && para encadenar."
            | ('\"' | '\'') as quote ->
                i <- i + 1
                let text = StringBuilder()
                let mutable closed = false
                while i < source.Length && not closed do
                    match source[i] with
                    | c when c = quote -> closed <- true; i <- i + 1
                    | '\\' ->
                        i <- i + 1
                        if i >= source.Length then LanguageError.fail start "Escape incompleto."
                        let c = match source[i] with 'n' -> '\n' | 't' -> '\t' | 'r' -> '\r' | '\\' -> '\\' | '\"' -> '\"' | '\'' -> '\'' | _ -> LanguageError.fail i "Escape desconocido."
                        text.Append c |> ignore
                        i <- i + 1
                    | c -> text.Append c |> ignore; i <- i + 1
                if not closed then LanguageError.fail start "Cadena sin cerrar."
                emit start (Word(text.ToString()))
            | _ ->
                while i < source.Length && not (special source[i]) do i <- i + 1
                if i = start then LanguageError.fail i "Carácter inesperado."
                emit start (Word(source.Substring(start,i-start)))
        emit source.Length Eof
        tokens.ToArray()

module Parser =
    let parse source : Result<Script,WqxError> = Errors.protect(fun () ->
        let tokens = Lexer.tokenize source
        let mutable i = 0
        let current () = tokens[i]
        let advance () = let t = tokens[i] in i <- i + 1; t
        let expect kind = if (current()).Kind <> kind then LanguageError.fail (current()).Position ("Se esperaba " + string kind) else advance() |> ignore
        let word () = match advance() with { Kind = Word value } -> value | t -> LanguageError.fail t.Position "Se esperaba un identificador o argumento."
        let skipSeparators () = while (current()).Kind = Separator do i <- i + 1
        let invocation () =
            let pos = (current()).Position
            if (current()).Kind = At then
                advance() |> ignore
                let subsystem = word()
                expect DoubleColon
                let action = word()
                expect OpenBrace
                skipSeparators()
                let mutable fields = Map.empty
                while (current()).Kind <> CloseBrace do
                    if (current()).Kind = Eof then LanguageError.fail pos "Bloque sin cerrar."
                    let key = word()
                    expect Colon
                    let value = word()
                    if fields.ContainsKey key then LanguageError.fail pos ("Campo duplicado: " + key)
                    fields <- fields.Add(key,value)
                    if (current()).Kind <> CloseBrace then expect Separator
                    skipSeparators()
                expect CloseBrace
                { Name = "@" + subsystem + "." + action; Arguments = []; Fields = fields; Position = pos }
            else
                let name = word()
                let args = ResizeArray<string>()
                while (match (current()).Kind with Word _ -> true | _ -> false) do args.Add(word())
                { Name = name; Arguments = List.ofSeq args; Fields = Map.empty; Position = pos }
        let pipelines = ResizeArray<Pipeline>()
        let mutable conditional = false
        skipSeparators()
        while (current()).Kind <> Eof do
            let commands = ResizeArray<Invocation>()
            commands.Add(invocation())
            while (current()).Kind = Pipe do advance() |> ignore; commands.Add(invocation())
            let destination = if (current()).Kind = Redirect then advance() |> ignore; Some(word()) else None
            pipelines.Add { Commands = List.ofSeq commands; Destination = destination; Conditional = conditional }
            match (current()).Kind with
            | And -> conditional <- true; advance() |> ignore; skipSeparators(); if (current()).Kind = Eof then LanguageError.fail (current()).Position "Falta una orden después de &&."
            | Separator -> conditional <- false; skipSeparators()
            | Eof -> ()
            | _ -> LanguageError.fail (current()).Position "Operador o final de instrucción esperado."
        List.ofSeq pipelines)

type LocalOperation =
    | Help of string option | Version | Clear | ChangeDirectory of string
    | SetVariable of string * string | SetAlias of string * string | RunScript of string
    | Login of string * string | Logout
    | Filter of string * string | Sort of string * bool | Select of string list | Take of int
type Instruction = SystemCall of Request | Local of LocalOperation
type CommandDefinition = { Name: string; Usage: string; Description: string; Resolve: string list -> Instruction }

module Numbers =
    let integer (s: string) = match Int32.TryParse s with true,n -> n | _ -> Errors.invalid ("Entero inválido: " + s)
    let size (s: string) =
        let m = Regex.Match(s,"^(\\d+)(B|KiB|MiB|GiB)?$",RegexOptions.IgnoreCase)
        if not m.Success then Errors.invalid "Tamaño inválido; use B, KiB, MiB o GiB."
        let n = match Int64.TryParse m.Groups[1].Value with true,n -> n | _ -> Errors.invalid "Tamaño fuera de rango."
        let unit = match m.Groups[2].Value.ToLowerInvariant() with "kib" -> 1024L | "mib" -> 1048576L | "gib" -> 1073741824L | _ -> 1L
        if n > Int64.MaxValue / unit then Errors.invalid "Tamaño fuera de rango."
        n * unit

type CommandRegistry() =
    let definitions = Dictionary<string,CommandDefinition>(StringComparer.Ordinal)
    let arity name usage f args =
        try f args with
        | :? MatchFailureException -> Errors.invalid (name + " " + usage)
    let add name usage description resolver = definitions.Add(name,{Name = name; Usage = usage; Description = description; Resolve = arity name usage resolver})
    do
        add "sys.info" "" "Estado del kernel y memoria lógica." (function [] -> SystemCall KernelInspect | _ -> Errors.invalid "sys.info")
        add "sys.off" "" "Apagado ordenado." (function [] -> SystemCall KernelShutdown | _ -> Errors.invalid "sys.off")
        add "sys.reboot" "" "Guarda y reinicia el entorno virtual." (function [] -> SystemCall KernelReboot | _ -> Errors.invalid "sys.reboot")
        add "sys.tick" "[count]" "Avanza ticks deterministas (administrador)." (function [] -> SystemCall(SchedulerTick 1) | [n] -> SystemCall(SchedulerTick(Numbers.integer n)) | _ -> Errors.invalid "sys.tick [count]")
        add "sys.config" "[key value]" "Consulta o modifica configuración validada." (function [] -> SystemCall ConfigGet | [k;v] -> SystemCall(ConfigSet(k,v)) | _ -> Errors.invalid "sys.config [key value]")
        add "proc.list" "" "Procesos visibles para la sesión." (function [] -> SystemCall ProcessList | _ -> Errors.invalid "proc.list")
        add "proc.spawn" "name [--priority n] [--memory size] [--steps n]" "Ejecuta un contador cooperativo finito en Ring 3." (fun args ->
            match args with
            | name::options ->
                let mutable priority,limit,steps = 4,67108864L,100
                let seen = HashSet<string>()
                let rec parse = function
                    | [] -> ()
                    | key::value::rest when seen.Add key ->
                        match key with "--priority" -> priority <- Numbers.integer value | "--memory" -> limit <- Numbers.size value | "--steps" -> steps <- Numbers.integer value | _ -> Errors.invalid ("Opción desconocida: " + key)
                        parse rest
                    | _ -> Errors.invalid "Opciones incompletas o duplicadas."
                parse options
                SystemCall(ProcessCreate(name,priority,limit,steps))
            | _ -> Errors.invalid "proc.spawn name [opciones]")
        for name,action in ["proc.end",End;"proc.pause",Pause;"proc.resume",Resume] do
            add name "pid" "Controla el ciclo de vida de un proceso." (function [pid] -> SystemCall(ProcessControl(Numbers.integer pid,action)) | _ -> Errors.invalid (name + " pid"))
        add "mem.map" "" "Regiones visibles de memoria lógica." (function [] -> SystemCall MemoryMap | _ -> Errors.invalid "mem.map")
        add "mem.alloc" "pid size" "Asigna una región lógica al proceso." (function [pid;size] -> SystemCall(MemoryAllocate(Numbers.integer pid,Numbers.size size)) | _ -> Errors.invalid "mem.alloc pid size")
        add "mem.free" "region" "Libera una región lógica autorizada." (function [id] -> SystemCall(MemoryRelease(Numbers.integer id)) | _ -> Errors.invalid "mem.free region")
        add "fs.list" "[path]" "Enumera archivos y metadatos." (function [] -> SystemCall(FileList ".") | [p] -> SystemCall(FileList p) | _ -> Errors.invalid "fs.list [path]")
        add "fs.go" "path" "Cambia el directorio de la sesión." (function [p] -> Local(ChangeDirectory p) | _ -> Errors.invalid "fs.go path")
        add "fs.new" "path" "Crea un directorio virtual." (function [p] -> SystemCall(DirectoryCreate p) | _ -> Errors.invalid "fs.new path")
        add "fs.read" "path" "Lee un archivo virtual." (function [p] -> SystemCall(FileRead p) | _ -> Errors.invalid "fs.read path")
        add "fs.write" "path text" "Crea o reemplaza un archivo virtual." (function [p;text] -> SystemCall(FileWrite(p,text)) | _ -> Errors.invalid "fs.write path text")
        add "fs.del" "path" "Elimina un archivo o directorio vacío." (function [p] -> SystemCall(FileDelete p) | _ -> Errors.invalid "fs.del path")
        add "fs.move" "source target" "Mueve o renombra un árbol autorizado." (function [a;b] -> SystemCall(FileMove(a,b)) | _ -> Errors.invalid "fs.move source target")
        add "fs.permit" "path true|false" "Configura lectura pública de una entrada propia." (function [p;"true"] -> SystemCall(FilePermission(p,true)) | [p;"false"] -> SystemCall(FilePermission(p,false)) | _ -> Errors.invalid "fs.permit path true|false")
        add "svc.list" "" "Estados, dependencias y actividad de servicios." (function [] -> SystemCall ServiceList | _ -> Errors.invalid "svc.list")
        for name,make in ["svc.start",ServiceStart;"svc.stop",ServiceStop;"svc.restart",ServiceRestart] do
            add name "name" "Administra un servicio y verifica dependencias." (function [n] -> SystemCall(make n) | _ -> Errors.invalid (name + " name"))
        add "dev.list" "" "Dispositivos del simulador." (function [] -> SystemCall DeviceList | _ -> Errors.invalid "dev.list")
        add "dev.info" "name" "Consulta un dispositivo virtual." (function [n] -> SystemCall(DeviceInfo n) | _ -> Errors.invalid "dev.info name")
        add "usr.info" "" "Identidad y capacidades vigentes." (function [] -> SystemCall IdentityQuery | _ -> Errors.invalid "usr.info")
        add "usr.list" "" "Usuarios, roles y grupos." (function [] -> SystemCall IdentityList | _ -> Errors.invalid "usr.list")
        add "usr.new" "name password" "Crea usuario con contraseña PBKDF2." (function [n;p] -> SystemCall(IdentityCreate(n,p)) | _ -> Errors.invalid "usr.new name password")
        add "usr.login" "name password" "Abre una sesión autenticada nueva." (function [n;p] -> Local(Login(n,p)) | _ -> Errors.invalid "usr.login name password")
        add "usr.logout" "" "Revoca la sesión actual y vuelve a guest." (function [] -> Local Logout | _ -> Errors.invalid "usr.logout")
        add "usr.grant" "name role" "Asigna un rol conocido; no concede Ring 0." (function [n;r] -> SystemCall(IdentityGrant(n,r)) | _ -> Errors.invalid "usr.grant name role")
        add "usr.revoke" "name role" "Revoca un rol con efecto inmediato." (function [n;r] -> SystemCall(IdentityRevoke(n,r)) | _ -> Errors.invalid "usr.revoke name role")
        add "log.show" "" "Registro técnico del kernel." (function [] -> SystemCall(LogRead false) | _ -> Errors.invalid "log.show")
        add "log.audit" "" "Decisiones de autorización y eventos de identidad." (function [] -> SystemCall(LogRead true) | _ -> Errors.invalid "log.audit")
        add "help" "[command]" "Ayuda contextual." (function [] -> Local(Help None) | [n] -> Local(Help(Some n)) | _ -> Errors.invalid "help [command]")
        add "version" "" "Versión de Wanqix." (function [] -> Local Version | _ -> Errors.invalid "version")
        add "clear" "" "Limpia la terminal." (function [] -> Local Clear | _ -> Errors.invalid "clear")
        add "set" "name value" "Define una variable de sesión, accesible como $name." (function [n;v] -> Local(SetVariable(n,v)) | _ -> Errors.invalid "set name value")
        add "alias" "name command" "Define un alias para un nombre de comando." (function [n;v] -> Local(SetAlias(n,v)) | _ -> Errors.invalid "alias name command")
        add "run" "path.ksl" "Ejecuta un script almacenado en WVFS." (function [p] -> Local(RunScript p) | _ -> Errors.invalid "run path.ksl")
        add "where" "column value" "Filtra igualdad tipada; texto sin distinguir mayúsculas." (function [k;v] -> Local(Filter(k,v)) | _ -> Errors.invalid "where column value")
        add "sort" "column [--desc]" "Ordena según el tipo de la columna." (function [k] -> Local(Sort(k,false)) | [k;"--desc"] -> Local(Sort(k,true)) | _ -> Errors.invalid "sort column [--desc]")
        add "select" "column ..." "Proyecta columnas de una tabla." (function [] -> Errors.invalid "select column ..." | cols -> Local(Select cols))
        add "take" "count" "Limita filas de una tabla." (function [n] -> Local(Take(Numbers.integer n)) | _ -> Errors.invalid "take count")
    member _.Definitions = definitions.Values |> Seq.sortBy _.Name |> Seq.toArray
    member _.Register definition =
        if definitions.ContainsKey definition.Name then Errors.invalid "Comando ya registrado."
        definitions.Add(definition.Name,definition)
    member _.Resolve(invocation: Invocation) =
        let short = Map.ofList ["sys","sys.info";"proc","proc.list";"mem","mem.map";"fs","fs.list";"dev","dev.list";"usr","usr.info";"svc","svc.list";"log","log.show"]
        let mutable name,args = invocation.Name,invocation.Arguments
        if name.StartsWith '@' then
            let fields = invocation.Fields
            let field key fallback = Map.tryFind key fields |> Option.defaultValue fallback
            let require key = match Map.tryFind key fields with Some s -> s | None -> LanguageError.fail invocation.Position ("Falta el campo " + key)
            let allowed =
                match name with
                | "@process.spawn" ->
                    name <- "proc.spawn"
                    args <- [require "identity";"--priority";field "priority" "4";"--memory";field "memory.limit" "64MiB";"--steps";field "steps" "100"]
                    Set.ofList ["identity";"priority";"memory.limit";"steps"]
                | "@memory.inspect" ->
                    if field "region" "all" <> "all" || field "format" "detailed" <> "detailed" then Errors.invalid "memory.inspect admite region: all y format: detailed."
                    name <- "mem.map"; Set.ofList ["region";"format"]
                | "@memory.allocate" -> name <- "mem.alloc"; args <- [require "pid";require "size"]; Set.ofList ["pid";"size"]
                | "@memory.release" -> name <- "mem.free"; args <- [require "region"]; Set.singleton "region"
                | "@filesystem.read" -> name <- "fs.read"; args <- [require "path"]; Set.singleton "path"
                | "@filesystem.write" -> name <- "fs.write"; args <- [require "path";require "content"]; Set.ofList ["path";"content"]
                | "@process.terminate" -> name <- "proc.end"; args <- [require "pid"]; Set.singleton "pid"
                | "@service.start" -> name <- "svc.start"; args <- [require "name"]; Set.singleton "name"
                | "@service.stop" -> name <- "svc.stop"; args <- [require "name"]; Set.singleton "name"
                | "@kernel.inspect" -> name <- "sys.info"; Set.empty
                | _ -> LanguageError.fail invocation.Position ("Instrucción estructurada desconocida: " + name)
            for KeyValue(key,_) in fields do if not (allowed.Contains key) then LanguageError.fail invocation.Position ("Campo no permitido: " + key)
        name <- Map.tryFind name short |> Option.defaultValue name
        match definitions.TryGetValue name with
        | true,definition -> definition.Resolve args
        | _ -> LanguageError.fail invocation.Position ("Comando desconocido: " + name)

type ShellSession(kernel: IKernel, token: Guid, login: string -> string -> Result<Guid,WqxError>, guest: unit -> Guid, logout: Guid -> unit, ?registry: CommandRegistry) =
    let registry = defaultArg registry (CommandRegistry())
    let variables = Dictionary<string,string>(StringComparer.Ordinal)
    let aliases = Dictionary<string,string>(StringComparer.Ordinal)
    let mutable token = token
    let mutable cwd = "/users/guest"
    let mutable user = "guest"
    let mutable clearRequested = false
    let mutable executionBudget = 0
    let unwrap = function Ok x -> x | Result.Error e -> raise(WqxException e)
    let call request = kernel.Call(token,request) |> unwrap
    let normalize (path: string) =
        // Actual canonicalization and protection are performed by WVFS after this resolution.
        if path.StartsWith '/' then path else cwd.TrimEnd('/') + "/" + path
    let expand (value: string) = Regex.Replace(value,"\\$\\{([a-zA-Z_][a-zA-Z0-9_]*)\\}|\\$([a-zA-Z_][a-zA-Z0-9_]*)",MatchEvaluator(fun m ->
        let key = if m.Groups[1].Success then m.Groups[1].Value else m.Groups[2].Value
        match variables.TryGetValue key with true,v -> v | _ -> Errors.invalid ("Variable no definida: " + key)))
    let paths = function
        | FileList p -> FileList(normalize p) | FileRead p -> FileRead(normalize p) | FileWrite(p,t) -> FileWrite(normalize p,t)
        | DirectoryCreate p -> DirectoryCreate(normalize p) | FileDelete p -> FileDelete(normalize p)
        | FileMove(a,b) -> FileMove(normalize a,normalize b) | FilePermission(p,b) -> FilePermission(normalize p,b) | r -> r
    let refreshUser () =
        let info = call IdentityQuery
        user <- info.Rows.Head["user"] |> Cell.text
        cwd <- "/users/" + user
    let safeName (name: string) = Regex.IsMatch(name,"^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}$")
    do refreshUser()
    member _.Token = token
    member _.User = user
    member _.Cwd = cwd
    member _.Registry = registry
    member _.ClearRequested with get() = clearRequested and set v = clearRequested <- v
    member _.SetVariable(name: string,value) =
        if not (Regex.IsMatch(name,"^[a-zA-Z_][a-zA-Z0-9_]{0,63}$")) then Errors.invalid "Nombre de variable inválido. Use letras, números y _."
        else variables[name] <- value
    member this.Execute(source: string, ?cancellation: CancellationToken) : Result<Table,WqxError> list =
        let cancellation = defaultArg cancellation CancellationToken.None
        executionBudget <- 10000
        this.ExecuteInternal(source,0,cancellation)
    member private this.ExecuteInternal(source,depth,cancellation: CancellationToken) : Result<Table,WqxError> list =
        if depth > 16 then [Result.Error { Code = "WQX-KSL-002"; Message = "Límite de recursión de scripts."; Position = None }]
        else
            match Parser.parse source with
            | Result.Error error -> [Result.Error error]
            | Ok script ->
                let outputs = ResizeArray<Result<Table,WqxError>>()
                let mutable previousSuccess = true
                for pipeline in script do
                    if not pipeline.Conditional || previousSuccess then
                        let result = Errors.protect(fun () ->
                            cancellation.ThrowIfCancellationRequested()
                            let mutable input: Table option = None
                            for invocation in pipeline.Commands do
                                cancellation.ThrowIfCancellationRequested()
                                executionBudget <- executionBudget - 1
                                if executionBudget < 0 then Errors.fail "WQX-KSL-002" "Presupuesto de instrucciones agotado."
                                let mutable name = invocation.Name
                                let seen = HashSet<string>()
                                while aliases.ContainsKey name do
                                    if not (seen.Add name) || seen.Count > 32 then Errors.invalid "Alias cíclico."
                                    name <- aliases[name]
                                let invocation = { invocation with Name = name; Arguments = List.map expand invocation.Arguments; Fields = Map.map (fun _ -> expand) invocation.Fields }
                                let instruction = registry.Resolve invocation
                                let requireInput () = match input with Some t -> t | None -> Errors.invalid "Este filtro requiere una tabla mediante |."
                                let requireColumn column (table: Table) = if not (List.contains column table.Columns) then Errors.invalid ("Columna desconocida: " + column)
                                let output =
                                    match instruction with
                                    | SystemCall request ->
                                        if input.IsSome then Errors.invalid "Las llamadas al sistema no consumen una canalización; use un filtro."
                                        call(paths request)
                                    | Local operation ->
                                        match operation with
                                        | Filter(column,value) ->
                                            let t = requireInput()
                                            requireColumn column t
                                            let matches (row: Map<string,Cell>) =
                                                match row[column] with
                                                | Number n -> match Int64.TryParse value with true,v -> n = v | _ -> false
                                                | Flag b -> match Boolean.TryParse value with true,v -> b = v | _ -> false
                                                | Text s -> String.Equals(s,value,StringComparison.OrdinalIgnoreCase)
                                            { t with Rows = List.filter matches t.Rows }
                                        | Sort(column,descending) ->
                                            let t = requireInput()
                                            requireColumn column t
                                            { t with Rows = t.Rows |> List.sortWith (fun a b -> let c = Cell.compare a[column] b[column] in if descending then -c else c) }
                                        | Select columns ->
                                            let t = requireInput()
                                            for c in columns do requireColumn c t
                                            { Columns = List.distinct columns; Rows = t.Rows |> List.map (Map.filter (fun k _ -> List.contains k columns)) }
                                        | Take count ->
                                            if count < 0 then Errors.invalid "take requiere un entero no negativo."
                                            let t = requireInput()
                                            { t with Rows = List.truncate count t.Rows }
                                        | _ when input.IsSome -> Errors.invalid "La orden no consume tablas."
                                        | Help name ->
                                            let definitions = registry.Definitions |> Array.filter (fun d -> name.IsNone || name = Some d.Name)
                                            if definitions.Length = 0 then Errors.invalid "No existe ayuda para ese comando."
                                            definitions |> Array.map (fun d -> [Text(d.Name + " " + d.Usage);Text d.Description]) |> Array.toList |> Table.create ["command";"description"]
                                        | Version -> Table.message "Wanqix 1.0.0 · F# 10 / .NET 10 · kernel simulado"
                                        | Clear -> clearRequested <- true; Table.message ""
                                        | ChangeDirectory path ->
                                            let table = call(FileList(normalize path))
                                            // WVFS validates access; obtain canonical directory from normalized segments.
                                            let parts = ResizeArray<string>()
                                            for p in (normalize path).Split('/',StringSplitOptions.RemoveEmptyEntries) do
                                                match p with
                                                | "." -> ()
                                                | ".." -> if parts.Count > 0 then parts.RemoveAt(parts.Count - 1)
                                                | _ -> parts.Add p
                                            let target = "/" + String.Join("/",parts)
                                            // FileList on a file returns that file itself; reject it for cwd.
                                            if table.Rows |> List.exists (fun r -> Cell.text r["path"] = target && Cell.text r["type"] = "file") then Errors.invalid "El destino no es un directorio."
                                            cwd <- target
                                            Table.message cwd
                                        | SetVariable(name,value) -> this.SetVariable(name,value); Table.message "Variable definida."
                                        | SetAlias(name,target) ->
                                            if not (safeName name && safeName target) || registry.Definitions |> Array.exists (fun d -> d.Name = name) then Errors.invalid "Alias inválido o comando reservado."
                                            aliases[name] <- target; Table.message "Alias definido."
                                        | RunScript path ->
                                            if not (path.EndsWith(".ksl",StringComparison.OrdinalIgnoreCase)) then Errors.invalid "Se esperaba un archivo .ksl."
                                            let table = call(FileRead(normalize path))
                                            let source = Cell.text table.Rows.Head["result"]
                                            let results = this.ExecuteInternal(source,depth + 1,cancellation)
                                            match results |> List.tryPick (function Result.Error e -> Some e | _ -> None) with
                                            | Some e -> raise(WqxException e)
                                            | None -> results |> List.tryLast |> Option.map unwrap |> Option.defaultValue (Table.message "Script vacío.")
                                        | Login(name,password) ->
                                            let newToken = login name password |> unwrap
                                            logout token
                                            token <- newToken
                                            variables.Clear(); aliases.Clear()
                                            refreshUser()
                                            Table.message ("Sesión: " + user)
                                        | Logout ->
                                            logout token
                                            token <- guest()
                                            variables.Clear(); aliases.Clear()
                                            refreshUser()
                                            Table.message "Sesión: guest"
                                input <- Some output
                            let output = input |> Option.defaultValue (Table.message "")
                            match pipeline.Destination with
                            | Some path ->
                                let escape (s: string) = s.Replace("\\","\\\\").Replace("\t","\\t").Replace("\r","\\r").Replace("\n","\\n")
                                let lines = String.concat "\t" output.Columns :: (output.Rows |> List.map (fun r -> output.Columns |> List.map (fun c -> escape(Cell.text r[c])) |> String.concat "\t"))
                                call(FileWrite(normalize(expand path),String.concat "\n" lines)) |> ignore
                            | None -> ()
                            output)
                        previousSuccess <- Result.isOk result
                        outputs.Add result
                List.ofSeq outputs
