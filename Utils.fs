/// General utilities
module FCSBenchmark.Generator.Utils

open System.Collections.Generic
open System.Diagnostics
open System.Threading
open Serilog.Events
    
let runProcess (name:string) (args:string) workingDir (envVariables : (string * string) list) (outputLogLevel : LogEventLevel) =
    let info = ProcessStartInfo()
    info.WindowStyle <- ProcessWindowStyle.Hidden
    info.Arguments <- args
    info.FileName <- name
    info.UseShellExecute <- false
    info.WorkingDirectory <- workingDir
    info.RedirectStandardError <- true
    info.RedirectStandardOutput <- true
    info.CreateNoWindow <- true

    envVariables |> List.iter (fun (k, v) -> info.EnvironmentVariables[ k ] <- v)

    log.Verbose ("Running '{name} {args}' in '{workingDir}'", name, args, workingDir)
    let p = new Process (StartInfo = info)
    p.EnableRaisingEvents <- true
    let output = List<string> ()

    p.OutputDataReceived.Add (fun args ->
        log.Write (outputLogLevel, args.Data)
        output.Add (args.Data)
    )

    p.ErrorDataReceived.Add (fun args -> log.Error (args.Data))
    p.Start () |> ignore
    p.BeginErrorReadLine ()
    p.BeginOutputReadLine ()
    p.WaitForExit ()

    if p.ExitCode <> 0 then
        if log.IsEnabled (outputLogLevel) then
            log.Error ("Process '{name} {args}' failed - check full process output above.", name, args)
        else
            log.Error ($"Process '{name} {args}' failed - printing its full output.", name, args)
            output |> Seq.iter log.Error

        failwith $"Process {name} {args} failed - check full process output above."
