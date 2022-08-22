module Benchmarks.Runner

open System
open System.IO
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Engines
open BenchmarkDotNet.Exporters.Json
open BenchmarkDotNet.Jobs
open FSharp.Compiler.CodeAnalysis
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open Benchmarks.Common.Dtos

[<MemoryDiagnoser>]
type FCSBenchmark () =
        
    let printDiagnostics (results : FSharpCheckFileResults) =
        match results.Diagnostics with
        | [||] -> ()
        | diagnostics ->
            printfn $"{diagnostics.Length} issues/diagnostics found:"
            for d in diagnostics do
                printfn $"- {d.Message}"
    
    let cleanCaches (checker : FSharpChecker) =
        checker.InvalidateAll()
        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
    
    let performAction (checker : FSharpChecker) (action : BenchmarkAction) =
        match action with
        | AnalyseFile x ->
            [1..x.Repeat]
            |> List.map (fun _ ->
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

let fcsPackageName = "FSharp.Compiler.Service"

module NuGet =
    [<RequireQualifiedAccess>]
    type NuGetFCSVersion =
        | Official of version : string
        | Local of sourceDir : string

    let officialNuGet (version : string) =
        NuGetReference(fcsPackageName, version, prerelease=true)

    let extractFCSNuPkgVersion (file : string) : string option =
        match System.Text.RegularExpressions.Regex.Match(file.ToLowerInvariant(), $".*{fcsPackageName.ToLowerInvariant()}.([0-9_\-\.]+).nupkg") with
        | res when res.Success -> res.Groups[1].Value |> Some
        | _ -> None
        
    let inferLocalNuGetVersion (sourceDir : string) =
        let files = Directory.EnumerateFiles(sourceDir, $"{fcsPackageName}.*.nupkg") |> Seq.toArray
        files
        |> Array.choose extractFCSNuPkgVersion
        |> Array.exactlyOne

    let localNuGet (sourceDir : string) =
        let version = inferLocalNuGetVersion sourceDir
        NuGetReference(fcsPackageName, version, Uri(sourceDir), prerelease=false)
                
    let makeReferenceList (version : NuGetFCSVersion) : NuGetReferenceList =
        match version with
        | NuGetFCSVersion.Official version -> officialNuGet version
        | NuGetFCSVersion.Local source -> localNuGet source
        |> fun ref -> NuGetReferenceList([ref])

let private defaultConfig () =
    let versions = [
        //NuGet.NuGetFCSVersion.Official "41.0.5"
        NuGet.NuGetFCSVersion.Local @"c:\projekty\fsharp\fsharp\artifacts\packages\Debug\Release\"
    ]
    let baseJob = Job.Dry
    let jobs =
        versions
        |> List.map NuGet.makeReferenceList
        |> List.map baseJob.WithNuGet
    
    let config = List.fold (fun (config : IConfig) (job : Job) -> config.AddJob(job)) DefaultConfig.Instance jobs
    config.AddExporter(JsonExporter(indentJson = true))

[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<FCSBenchmark>.Assembly).Run(args, defaultConfig())
    |> ignore
    0