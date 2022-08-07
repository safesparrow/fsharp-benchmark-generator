module Benchmarks.Runner

open System
open System.Diagnostics
open System.IO
open FSharp.Compiler.CodeAnalysis
open Benchmarks.Common.Dtos

type FCSBenchmark (config : BenchmarkConfig) =
    let checker = FSharpChecker.Create(projectCacheSize = config.ProjectCacheSize)
        
    let printDiagnostics (results : FSharpCheckFileResults) =
        match results.Diagnostics with
        | [||] ->
            printfn $"No issues found in code to report."
        | diagnostics ->
            printfn $"{results.Diagnostics.Length} issues/diagnostics found:"
            for d in results.Diagnostics do
                printfn $"- {d.Message}"
    
    let performAction (action : BenchmarkAction) =
        let sw = Stopwatch.StartNew()
        let res =
            match action with
            | AnalyseFile x ->
                let result, answer =
                    checker.ParseAndCheckFileInProject(x.FileName, x.FileVersion, FSharp.Compiler.Text.SourceText.ofString x.SourceText, x.Options)
                    |> Async.RunSynchronously
                match answer with
                | FSharpCheckFileAnswer.Aborted -> failwith "checker aborted"
                | FSharpCheckFileAnswer.Succeeded results ->
                    printDiagnostics results
                action, ((result, answer) :> Object)
        res
            
    let cleanCaches () =
        checker.InvalidateAll()
        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
        
    member this.Checker = checker
    member this.PerformAction action = performAction action
    member this.CleanCaches () = cleanCaches

let runIteration (inputs : BenchmarkInputs) =
    let sw = Stopwatch.StartNew()
    let b = FCSBenchmark(inputs.Config)
    let outputs =
        inputs.Actions
        |> List.mapi (fun i action ->
            printfn $"[{i}] Action: start"
            let output = b.PerformAction action
            printfn $"[{i}] Action: took {sw.ElapsedMilliseconds}ms"
            output
        )
    printfn $"Performed {outputs.Length} action(s) in {sw.ElapsedMilliseconds}ms"
    ()

[<EntryPoint>]
let main args =
    match args with
    | [|inputFile; iterations|] ->
        let iterations = Int32.Parse iterations
        printfn $"Deserializing inputs from '{inputFile}'"
        let json = File.ReadAllText(inputFile)
        let inputs = deserializeInputs json
        printfn $"Running {iterations} iteration(s) of the benchmark, each containing {inputs.Actions.Length} action(s)"
        let sw = Stopwatch.StartNew()
        for i in 1..iterations do
            runIteration inputs
        sw.Stop()
        let meanIterationTimeMs = sw.ElapsedMilliseconds/(int64)iterations
        printfn $"Performed {iterations} iteration(s) in {sw.ElapsedMilliseconds} - averaging {meanIterationTimeMs}ms per iteration"
        0
    | _ ->
        printfn $"Invalid args: %A{args}. Expected format: 'dotnet run [input file.json] [iterations]'"
        1
    