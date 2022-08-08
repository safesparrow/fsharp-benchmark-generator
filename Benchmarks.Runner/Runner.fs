﻿module Benchmarks.Runner

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open Benchmarks.Common.Dtos

[<MemoryDiagnoser>]
type FCSBenchmark () =
        
    let printDiagnostics (results : FSharpCheckFileResults) =
        match results.Diagnostics with
        | [||] ->
            printfn $"No issues found in code to report."
        | diagnostics ->
            printfn $"{results.Diagnostics.Length} issues/diagnostics found:"
            for d in results.Diagnostics do
                printfn $"- {d.Message}"
    
    let cleanCaches (checker : FSharpChecker) =
        checker.InvalidateAll()
        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
    
    let performAction (checker : FSharpChecker) (action : BenchmarkAction) =
        match action with
        | AnalyseFile x ->
            [1..x.Repeat]
            |> List.map (fun it ->
                printfn $"Iteration {it}"
                let result, answer =
                    checker.ParseAndCheckFileInProject(x.FileName, x.FileVersion, FSharp.Compiler.Text.SourceText.ofString x.SourceText, x.Options)
                    |> Async.RunSynchronously
                match answer with
                | FSharpCheckFileAnswer.Aborted -> failwith "checker aborted"
                | FSharpCheckFileAnswer.Succeeded results ->
                    printDiagnostics results
                    cleanCaches checker
                    action, (result, answer)
            )
            
    let mutable setup : (FSharpChecker * BenchmarkAction list) option = None
        
    [<GlobalSetup>]
    member _.Setup() =
        let inputFile = Environment.GetEnvironmentVariable("FcsBenchmarkInput")
        if File.Exists(inputFile) then
            printfn $"Deserializing inputs from '{inputFile}'"
            let json = File.ReadAllText(inputFile)
            let inputs = deserializeInputs json
            let checker = FSharpChecker.Create(projectCacheSize = inputs.Config.ProjectCacheSize)
            setup <- (checker, inputs.Actions) |> Some
        else
            failwith $"Input file '{inputFile}' does not exist"

    [<Benchmark>]
    member _.Run() =
        match setup with
        | None -> failwith "Setup did not run"
        | Some (checker, actions) ->
            for action in actions do
                performAction checker action
                |> ignore

    [<IterationCleanup>]
    member _.Cleanup() =
        setup
        |> Option.iter (fun (checker, _) -> cleanCaches checker)

[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<FCSBenchmark>.Assembly).Run(args) |> ignore
    0