/// General utilities
module FCSBenchmark.Generator.Utils

open System.Diagnostics
open Serilog.Events
    
let runProcess name args workingDir (envVariables : (string * string) list) (outputLogLevel : LogEventLevel) =
    let info = ProcessStartInfo()
    info.WindowStyle <- ProcessWindowStyle.Hidden
    info.Arguments <- args
    info.FileName <- name
    info.UseShellExecute <- false
    info.WorkingDirectory <- workingDir
    info.RedirectStandardError <- true
    info.RedirectStandardOutput <- true
    info.CreateNoWindow <- true
    
    envVariables
    |> List.iter (fun (k, v) -> info.EnvironmentVariables[k] <- v)
    
    log.Verbose("Running '{name} {args}' in '{workingDir}'", name, args, workingDir)
    let p = new Process(StartInfo = info)
    p.EnableRaisingEvents <- true
    p.OutputDataReceived.Add(fun args -> log.Write(outputLogLevel, args.Data))
    p.ErrorDataReceived.Add(fun args -> log.Error(args.Data))
    p.Start() |> ignore
    p.BeginErrorReadLine()
    p.BeginOutputReadLine()
    p.WaitForExit()
    
    if p.ExitCode <> 0 then
        log.Error("Process '{name} {args}' failed - check full process output above.", name, args)
        failwith $"Process {name} {args} failed - check full process output above."
