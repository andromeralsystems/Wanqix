namespace Wanqix.Terminal

open System
open System.Text
open Wanqix.Core
open Wanqix.KSL

module Render =
    /// Never render user-controlled ANSI/OSC or terminal control sequences.
    let safe (text: string) =
        let output = StringBuilder()
        for c in text do
            if Char.IsControl c || c = '\u007f' || c = '\u202e' || c = '\u202d' then output.Append(sprintf "\\u%04x" (int c)) |> ignore
            else output.Append c |> ignore
        output.ToString()
    let lifecycle color (entry: LifecycleEvent) =
        let status,ansi =
            match entry.Status with
            | Starting -> " .... ","36"
            | Completed -> "  OK  ","32"
            | Notice -> " INFO ","33"
            | StepFailed -> "FAILED","31"
        let badge = if color && not Console.IsErrorRedirected then "\u001b[" + ansi + "m" + status + "\u001b[0m" else status
        let detail = if String.IsNullOrEmpty entry.Detail then "" else " — " + safe entry.Detail
        Console.Error.WriteLine(sprintf "[%9.3fs] [%s] %-6s %s%s" entry.Elapsed.TotalSeconds badge (safe entry.Phase) (safe entry.Operation) detail)
        Console.Error.Flush()
    let table (value: Table) =
        if value.Columns = ["result"] then
            for row in value.Rows do printfn "%s" (safe(Cell.text row["result"]))
        else
            let maxWidth = if Console.IsOutputRedirected then 160 else max 40 (Console.WindowWidth - 1)
            let perColumn = max 8 (min 48 ((maxWidth - value.Columns.Length * 3) / max 1 value.Columns.Length))
            let clip (s: string) = let s = safe s in if s.Length > perColumn then s.Substring(0,max 1 (perColumn - 1)) + "…" else s
            let rows = value.Rows |> List.map (fun row -> value.Columns |> List.map (fun c -> clip(Cell.text row[c])))
            let header = value.Columns |> List.map clip
            let widths = header |> List.mapi (fun i h -> max h.Length (rows |> List.map (fun row -> row[i].Length) |> List.fold max 0))
            let line columns = List.map2 (fun (s: string) width -> s.PadRight width) columns widths |> String.concat " │ " |> printfn "%s"
            line header
            widths |> List.map (fun w -> String('─',w)) |> String.concat "─┼─" |> printfn "%s"
            for row in rows do line row
            printfn "(%d filas)" value.Rows.Length
    let error color (error: WqxError) =
        let position = error.Position |> Option.map (sprintf " [posición %d]") |> Option.defaultValue ""
        let text = error.Code + position + ": " + safe error.Message
        if color && not Console.IsOutputRedirected then eprintfn "\u001b[31m%s\u001b[0m" text else eprintfn "%s" text
    let progress label step total =
        if not Console.IsOutputRedirected then printf "\r\u001b[2K%s [%s%s] %d/%d" label (String('█',step)) (String('░',max 0 (total-step))) step total
        if step = total then printfn ""

type LineEditor(commands: unit -> string array, initialHistory: string array) =
    let history = ResizeArray<string>(initialHistory)
    member _.Remember text =
        if history.Count = 0 || history[history.Count - 1] <> text then history.Add text
        while history.Count > 500 do history.RemoveAt 0
    member _.ReadSecret(prompt: string) =
        Console.Write prompt
        if Console.IsInputRedirected then Console.ReadLine()
        else
            let text = StringBuilder()
            let mutable doneReading = false
            while not doneReading do
                let key = Console.ReadKey true
                match key.Key with
                | ConsoleKey.Enter -> doneReading <- true
                | ConsoleKey.Backspace when text.Length > 0 -> text.Remove(text.Length - 1,1) |> ignore
                | _ when not (Char.IsControl key.KeyChar) -> text.Append key.KeyChar |> ignore
                | _ -> ()
            Console.WriteLine()
            text.ToString()
    member _.ReadLine(prompt: string, color: bool) =
        if Console.IsInputRedirected then Console.ReadLine()
        else
            Console.TreatControlCAsInput <- true
            let text = StringBuilder()
            let mutable cursor = 0
            let mutable historyIndex = history.Count
            let mutable draft = ""
            let mutable finished = false
            let mutable eof = false
            let mutable renderedRows = 0
            let mutable previousWidth = max 1 Console.WindowWidth
            let mutable completionIndex = -1
            let mutable completionPrefix = ""
            let mutable completionSuffix = ""
            let mutable completionChoices: string array = [||]
            let redraw () =
                let width = max 1 Console.WindowWidth
                // Return to the start of our previous prompt, then render only the editor area.
                if renderedRows > 0 then Console.Write(sprintf "\u001b[%dA" renderedRows)
                Console.Write "\r\u001b[J"
                let visible = Render.safe(text.ToString())
                if color then Console.Write("\u001b[36m" + prompt + "\u001b[0m" + visible) else Console.Write(prompt + visible)
                let length = prompt.Length + visible.Length
                let endRow = length / width
                let before = Render.safe(text.ToString(0,cursor))
                let cursorOffset = prompt.Length + before.Length
                let cursorRow = cursorOffset / width
                if endRow > cursorRow then Console.Write(sprintf "\u001b[%dA" (endRow-cursorRow))
                Console.Write(sprintf "\u001b[%dG" (cursorOffset % width + 1))
                renderedRows <- cursorRow
                previousWidth <- width
            redraw()
            while not finished do
                let key = Console.ReadKey true
                if key.Key <> ConsoleKey.Tab then completionIndex <- -1
                match key.Key with
                | ConsoleKey.C when key.Modifiers.HasFlag ConsoleModifiers.Control ->
                    text.Clear() |> ignore
                    cursor <- 0
                    redraw()
                    Console.WriteLine("^C")
                    finished <- true
                | ConsoleKey.D when key.Modifiers.HasFlag ConsoleModifiers.Control && text.Length = 0 -> eof <- true; finished <- true; Console.WriteLine()
                | ConsoleKey.Enter ->
                    cursor <- text.Length
                    redraw()
                    Console.WriteLine()
                    finished <- true
                | ConsoleKey.LeftArrow when cursor > 0 -> cursor <- cursor - 1
                | ConsoleKey.RightArrow when cursor < text.Length -> cursor <- cursor + 1
                | ConsoleKey.Home -> cursor <- 0
                | ConsoleKey.End -> cursor <- text.Length
                | ConsoleKey.Backspace when cursor > 0 -> text.Remove(cursor - 1,1) |> ignore; cursor <- cursor - 1
                | ConsoleKey.Delete when cursor < text.Length -> text.Remove(cursor,1) |> ignore
                | ConsoleKey.UpArrow when historyIndex > 0 ->
                    if historyIndex = history.Count then draft <- text.ToString()
                    historyIndex <- historyIndex - 1
                    text.Clear().Append(history[historyIndex]) |> ignore
                    cursor <- text.Length
                | ConsoleKey.DownArrow when historyIndex < history.Count ->
                    historyIndex <- historyIndex + 1
                    text.Clear().Append(if historyIndex = history.Count then draft else history[historyIndex]) |> ignore
                    cursor <- text.Length
                | ConsoleKey.Tab ->
                    if completionIndex < 0 then
                        let before = text.ToString(0,cursor)
                        let start = before.LastIndexOfAny([|' ';'\t';'|';';'|]) + 1
                        completionPrefix <- before.Substring(0,start)
                        completionSuffix <- text.ToString(cursor,text.Length-cursor)
                        let prefix = before.Substring start
                        completionChoices <- commands() |> Array.filter (fun c -> c.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                    if completionChoices.Length > 0 then
                        completionIndex <- (completionIndex + 1) % completionChoices.Length
                        text.Clear().Append(completionPrefix).Append(completionChoices[completionIndex]).Append(completionSuffix) |> ignore
                        cursor <- completionPrefix.Length + completionChoices[completionIndex].Length
                | _ when not (Char.IsControl key.KeyChar) && text.Length < 4096 -> text.Insert(cursor,key.KeyChar) |> ignore; cursor <- cursor + 1
                | _ -> ()
                // ReadKey blocks in idle; resizing is accounted for on the next editing event.
                if not finished then
                    if previousWidth <> Console.WindowWidth then renderedRows <- min renderedRows Console.CursorTop
                    redraw()
            Console.TreatControlCAsInput <- false
            if eof then null else text.ToString()
