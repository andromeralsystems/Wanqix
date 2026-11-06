namespace Wanqix.Security

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.RegularExpressions
open Wanqix.Core

type Authority(journal: EventJournal, users: UserRecord array, roles: RoleRecord array, clock: IClock) =
    let users = Dictionary<string,UserRecord>(users |> Array.map (fun u -> KeyValuePair(u.Name,u)), StringComparer.Ordinal)
    let roles = Dictionary<string,RoleRecord>(roles |> Array.map (fun r -> KeyValuePair(r.Name,r)), StringComparer.Ordinal)
    let sessions = Dictionary<Guid,string * Ring * DateTimeOffset>()
    let serviceContexts = Dictionary<Guid,SecurityContext>()
    let attempts = Dictionary<string,int * DateTimeOffset>()
    let validName (name: string) = Regex.IsMatch(name, "^[a-z][a-z0-9._-]{0,31}$")
    let capabilities (user: UserRecord) = user.Roles |> Seq.collect (fun r -> if roles.ContainsKey r then roles[r].Capabilities else [||]) |> Set.ofSeq
    let issue name ring =
        let token = Guid.NewGuid()
        sessions.Add(token,(name,ring,clock.UtcNow.AddHours 12.))
        journal.Write(Info,"IdentitySession",name,"session","Sesión creada.")
        token
    static member CreateUser(name, password, assignedRoles) =
        if String.IsNullOrWhiteSpace password || password.Length < 12 || password.Length > 1024 then Errors.invalid "La contraseña requiere entre 12 y 1024 caracteres."
        let salt = RandomNumberGenerator.GetBytes 32
        let hash = Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32)
        { Id = Guid.NewGuid(); Name = name; Roles = assignedRoles; Groups = [|"users"|]; Salt = Convert.ToBase64String salt; Hash = Convert.ToBase64String hash; Iterations = 210000; Disabled = false }
    static member DefaultRoles =
        let user = [|"kernel.inspect"; "process.create"; "process.list"; "process.terminate"; "memory.inspect"; "memory.allocate"; "filesystem.read"; "filesystem.write"; "identity.query"; "service.query"; "device.query"|]
        [| { Name = "user"; Capabilities = user }
           { Name = "admin"; Capabilities = Array.append user [|"kernel.configure"; "kernel.shutdown"; "process.manage"; "memory.manage"; "filesystem.manage"; "service.manage"; "security.audit.read"; "log.read"; "identity.manage"|] } |]
    member _.Guest() = issue "guest" Ring.User
    /// Composition-only API. The authority instance and returned tokens are owned by the kernel.
    /// Compiled-in services are trusted; KSL cannot register a service or choose an execution ring.
    member _.RegisterSystemService(name,capabilities: string array) =
        let token = Guid.NewGuid()
        serviceContexts.Add(token,{ User = "system." + name; Ring = Ring.System; Capabilities = Set.ofArray capabilities; Session = token })
        token
    member _.Login(name: string, password: string) =
        if not (validName name) || isNull password || password.Length > 1024 then Errors.denied()
        let previous,last = match attempts.TryGetValue name with true,v -> v | _ -> 0,DateTimeOffset.MinValue
        let count = if clock.UtcNow - last >= TimeSpan.FromMinutes 1. then 0 else previous
        if count >= 5 && clock.UtcNow - last < TimeSpan.FromMinutes 1. then Errors.fail "WQX-SEC-003" "Autenticación temporalmente bloqueada."
        let success =
            match users.TryGetValue name with
            | true,u when not u.Disabled ->
                let hash = Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromBase64String u.Salt,u.Iterations,HashAlgorithmName.SHA256,32)
                CryptographicOperations.FixedTimeEquals(hash,Convert.FromBase64String u.Hash)
            | _ ->
                Rfc2898DeriveBytes.Pbkdf2(password,Array.zeroCreate<byte> 32,210000,HashAlgorithmName.SHA256,32) |> ignore
                false
        if not success then
            if attempts.Count >= 1000 && not (attempts.ContainsKey name) then attempts.Remove(attempts |> Seq.minBy (fun kv -> snd kv.Value) |> _.Key) |> ignore
            attempts[name] <- (count + 1, clock.UtcNow)
            journal.Write(Warning,"SecurityViolation",name,"login","Autenticación rechazada.")
            Errors.denied()
        attempts.Remove name |> ignore
        let ring = if (capabilities users[name]).Contains "identity.manage" then Ring.Administrative else Ring.User
        issue name ring
    member _.Logout(token) = sessions.Remove token |> ignore
    member _.Context token =
        match serviceContexts.TryGetValue token,sessions.TryGetValue token with
        | (true,context),_ -> context
        | _,(true,(name,ring,expires)) when expires > clock.UtcNow && users.ContainsKey name && not users[name].Disabled ->
            { User = name; Ring = ring; Capabilities = capabilities users[name]; Session = token }
        | _ ->
            journal.Write(Warning,"SecurityViolation","unknown","session","Sesión inválida o expirada.")
            Errors.denied()
    member this.Authorize token operation target =
        let context = this.Context token
        let adminOperation = Set.ofList ["kernel.configure";"kernel.shutdown";"service.manage";"identity.manage";"security.audit.read";"log.read";"process.manage";"memory.manage";"filesystem.manage"]
        if not (context.Capabilities.Contains operation) || (adminOperation.Contains operation && int context.Ring > int Ring.Administrative) then
            journal.Write(Warning,"SecurityViolation",context.User,target,operation + " denied")
            Errors.denied()
        journal.Write(Debug,"Authorization",context.User,target,operation + " allowed")
        context
    member this.Users(token) = this.Authorize token "identity.manage" "users" |> ignore; users.Values |> Seq.toArray
    member this.Create(token,name,password) =
        this.Authorize token "identity.manage" name |> ignore
        if not (validName name) || users.ContainsKey name then Errors.invalid "Nombre de usuario inválido o existente."
        users.Add(name,Authority.CreateUser(name,password,[|"user"|]))
        journal.Write(Info,"IdentityChanged",name,"users","Usuario creado.")
    member this.ChangeRole(token,name,role,grant) =
        this.Authorize token "identity.manage" name |> ignore
        if not (users.ContainsKey name) || not (roles.ContainsKey role) then Errors.invalid "Usuario o rol inexistente."
        if name = "root" then Errors.denied()
        let u = users[name]
        let updated = if grant then Set.add role (Set.ofArray u.Roles) else Set.remove role (Set.ofArray u.Roles)
        users[name] <- { u with Roles = Set.toArray updated }
        journal.Write(Info,"IdentityChanged",name,role,if grant then "Rol concedido." else "Rol revocado.")
    member _.ExportUsers() = users.Values |> Seq.toArray
    member _.ExportRoles() = roles.Values |> Seq.toArray
    member _.Close() = sessions.Clear(); serviceContexts.Clear()
