namespace Wanqix.Host.Windows

open System
open System.IO
open System.Text

module WindowsHost =
    let initialize () =
        Console.InputEncoding <- UTF8Encoding(false)
        Console.OutputEncoding <- UTF8Encoding(false)
        if OperatingSystem.IsWindows() && not Console.IsOutputRedirected then Console.Title <- "Wanqix 1.0 — kernel simulado"
    let defaultDataDirectory () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Wanqix","1.0")
