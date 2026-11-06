namespace Wanqix.Storage

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Wanqix.Core

type IStateStore =
    abstract Load<'T> : validate:('T -> unit) -> 'T option * bool
    abstract Save<'T> : state:'T -> unit

/// Only two fixed snapshot names are used. Virtual paths never reach host file APIs.
type AtomicStateStore(directory: string) =
    let root = Path.GetFullPath directory
    let state = Path.Combine(root,"state.v1.json")
    let backup = Path.Combine(root,"state.v1.backup.json")
    let options = JsonSerializerOptions(WriteIndented = true, MaxDepth = 32)
    let mutable recovering = false
    let check () =
        let rejectLink (path: string) =
            try
                if (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint then Errors.fail "WQX-FS-004" "El almacenamiento no admite enlaces simbólicos, incluso sin destino."
            with
            | :? FileNotFoundException -> ()
            | :? DirectoryNotFoundException -> ()
        let mutable current = DirectoryInfo root
        while not (isNull current) do
            rejectLink current.FullName
            current <- current.Parent
        for path in [state;backup;state + ".tmp";Path.Combine(root,"instance.lock")] do
            rejectLink path
    do
        check()
        Directory.CreateDirectory root |> ignore
    let lease =
        try new FileStream(Path.Combine(root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
        with :? IOException -> Errors.fail "WQX-STO-002" "El directorio de datos está en uso o no admite acceso exclusivo."
    interface IDisposable with member _.Dispose() = lease.Dispose()
    interface IStateStore with
        member _.Load<'T>(validate: 'T -> unit) =
            check()
            let read path =
                if FileInfo(path).Length > 64L * 1024L * 1024L then Errors.fail "WQX-STO-001" "Snapshot demasiado grande."
                let value = JsonSerializer.Deserialize<'T>(File.ReadAllText(path,Encoding.UTF8),options)
                validate value
                value
            if not (File.Exists state) then
                if File.Exists backup then
                    try recovering <- true; Some(read backup),true
                    with _ -> Errors.fail "WQX-STO-001" "No existe estado primario y el respaldo es inválido."
                else None,false
            else
                try Some(read state),false
                with _ ->
                    if File.Exists backup then
                        try recovering <- true; Some(read backup),true with _ -> Errors.fail "WQX-STO-001" "Estado y respaldo inválidos. Conserve los archivos y seleccione otro --data para recuperación manual."
                    else Errors.fail "WQX-STO-001" "Estado dañado sin respaldo válido. Conserve el archivo y seleccione otro --data."
        member _.Save<'T>(value: 'T) =
            check()
            let temp = state + ".tmp"
            let data = JsonSerializer.SerializeToUtf8Bytes(value,options)
            if data.LongLength > 64L * 1024L * 1024L then Errors.fail "WQX-STO-001" "Se alcanzó el límite del snapshot."
            use stream = new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)
            stream.Write data
            stream.Flush true
            stream.Dispose()
            if recovering then
                File.Move(temp,state,true)
                recovering <- false
            elif File.Exists state then File.Replace(temp,state,backup)
            else File.Move(temp,state)

module VirtualPath =
    let normalize (cwd: string) (path: string) =
        if String.IsNullOrWhiteSpace path || path.Length > 1024 || path |> Seq.exists (fun c -> Char.IsControl c || c = '\\' || c = ':' || c = '*' || c = '?') then Errors.fail "WQX-FS-004" "Ruta virtual inválida."
        let full = if path.StartsWith '/' then path else cwd.TrimEnd('/') + "/" + path
        let parts = ResizeArray<string>()
        for part in full.Split('/',StringSplitOptions.RemoveEmptyEntries) do
            match part with
            | "." -> ()
            | ".." -> if parts.Count = 0 then Errors.fail "WQX-FS-004" "La ruta escapa de la raíz virtual." else parts.RemoveAt(parts.Count - 1)
            | value when value.Length > 128 -> Errors.fail "WQX-FS-004" "Componente de ruta demasiado largo."
            | value -> parts.Add value
        "/" + String.Join("/",parts)
    let parent (path: string) = let index = path.LastIndexOf '/' in if index <= 0 then "/" else path.Substring(0,index)

type VirtualFileSystem(guard: Guard, clock: IClock, journal: EventJournal, initial: FileEntry array) =
    let files = Dictionary<string,FileEntry>(StringComparer.Ordinal)
    let protectedPaths = Set.ofList ["/";"/system";"/system/config";"/system/logs";"/system/services";"/users";"/users/root";"/users/guest";"/apps";"/temp";"/devices"]
    let get path = match files.TryGetValue path with true,f -> f | _ -> Errors.fail "WQX-FS-005" "Entrada virtual inexistente."
    let allowed (c: SecurityContext) write (f: FileEntry) =
        c.Capabilities.Contains "filesystem.manage" || f.Owner = c.User || (not write && f.PublicRead) || (write && f.Path = "/temp")
    let access token op path write =
        let c = guard token op path
        let f = get path
        if not (allowed c write f) then Errors.denied()
        let mutable parent = VirtualPath.parent path
        while parent <> "/" do
            if not (allowed c false (get parent)) then Errors.denied()
            parent <- VirtualPath.parent parent
        c,f
    let validateParent token path =
        let c,p = access token "filesystem.write" (VirtualPath.parent path) true
        if not p.Directory then Errors.fail "WQX-FS-006" "El padre no es un directorio."
        c
    let changed actor path = journal.Write(Info,"FileChanged",actor,path,"WVFS actualizado.")
    do
        if initial.Length = 0 then
            for path in protectedPaths do
                let owner = if path = "/users/guest" then "guest" else "root"
                files.Add(path,{ Path = path; Directory = true; Owner = owner; PublicRead = path <> "/users/root" && path <> "/users/guest"; Content = ""; Modified = clock.UtcNow })
        else
            for f in initial do
                if VirtualPath.normalize "/" f.Path <> f.Path || files.ContainsKey f.Path || isNull f.Content || f.Content.Length > 1048576 then Errors.fail "WQX-STO-001" "Metadatos WVFS inválidos."
                files.Add(f.Path,f)
            for path in protectedPaths do if not (files.ContainsKey path) || not files[path].Directory then Errors.fail "WQX-STO-001" "Estructura WVFS incompleta."
            for f in initial do if f.Path <> "/" && (not (files.ContainsKey (VirtualPath.parent f.Path)) || not files[VirtualPath.parent f.Path].Directory) then Errors.fail "WQX-STO-001" "Árbol WVFS inválido."
    member _.List(token,path) =
        let path = VirtualPath.normalize "/" path
        let c,f = access token "filesystem.read" path false
        if not f.Directory then [|f|]
        else files.Values |> Seq.filter (fun f -> f.Path <> path && VirtualPath.parent f.Path = path && allowed c false f) |> Seq.sortBy _.Path |> Seq.toArray
    member _.Read(token,path) =
        let _,f = access token "filesystem.read" (VirtualPath.normalize "/" path) false
        if f.Directory then Errors.fail "WQX-FS-006" "Se esperaba un archivo."
        f.Content
    member _.Write(token,path,content: string) =
        let path = VirtualPath.normalize "/" path
        if isNull content || content.Length > 1048576 then Errors.fail "WQX-FS-007" "El archivo supera 1 MiB de caracteres."
        let c = validateParent token path
        if files.Count >= 10000 && not (files.ContainsKey path) then Errors.fail "WQX-FS-007" "Límite de entradas alcanzado."
        let f =
            if files.ContainsKey path then
                let _,f = access token "filesystem.write" path true
                if f.Directory then Errors.fail "WQX-FS-006" "No se puede escribir sobre un directorio."
                { f with Content = content; Modified = clock.UtcNow }
            else { Path = path; Directory = false; Owner = c.User; PublicRead = false; Content = content; Modified = clock.UtcNow }
        files[path] <- f
        changed c.User path
    member _.Mkdir(token,path) =
        let path = VirtualPath.normalize "/" path
        let c = validateParent token path
        if files.ContainsKey path || files.Count >= 10000 then Errors.fail "WQX-FS-006" "La entrada existe o se alcanzó el límite."
        files.Add(path,{ Path = path; Directory = true; Owner = c.User; PublicRead = false; Content = ""; Modified = clock.UtcNow })
        changed c.User path
    member _.Delete(token,path) =
        let path = VirtualPath.normalize "/" path
        let c,_ = access token "filesystem.write" path true
        validateParent token path |> ignore
        if protectedPaths.Contains path then Errors.denied()
        if files.Keys |> Seq.exists (fun p -> p.StartsWith(path + "/",StringComparison.Ordinal)) then Errors.fail "WQX-FS-006" "El directorio no está vacío."
        files.Remove path |> ignore
        changed c.User path
    member _.Move(token,source,target) =
        let source,target = VirtualPath.normalize "/" source,VirtualPath.normalize "/" target
        let c,_ = access token "filesystem.write" source true
        validateParent token source |> ignore
        validateParent token target |> ignore
        if protectedPaths.Contains source || target.StartsWith(source + "/",StringComparison.Ordinal) || files.ContainsKey target then Errors.fail "WQX-FS-006" "Movimiento inválido."
        let subtree = files.Values |> Seq.filter (fun f -> f.Path = source || f.Path.StartsWith(source + "/",StringComparison.Ordinal)) |> Seq.toArray
        for f in subtree do if not (allowed c true f) then Errors.denied()
        for f in subtree do files.Remove f.Path |> ignore
        for f in subtree do let path = target + f.Path.Substring(source.Length) in files.Add(path,{ f with Path = path; Modified = clock.UtcNow })
        changed c.User target
    member _.Permission(token,path,publicRead) =
        let path = VirtualPath.normalize "/" path
        let c,f = access token "filesystem.write" path true
        if protectedPaths.Contains path then Errors.denied()
        files[path] <- { f with PublicRead = publicRead }
        changed c.User path
    member _.EnsureHome(name) =
        let path = "/users/" + name
        if not (files.ContainsKey path) then files.Add(path,{ Path = path; Directory = true; Owner = name; PublicRead = false; Content = ""; Modified = clock.UtcNow })
    member _.Export() = files.Values |> Seq.sortBy _.Path |> Seq.toArray
