module Benchmarks.Runner

open System
open System.IO
open System.Threading
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Engines
open BenchmarkDotNet.Exporters.Json
open BenchmarkDotNet.Jobs
open FSharp.Compiler.CodeAnalysis
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open Benchmarks.Common.Dtos
open CommandLine
open NuGet.Packaging.Core
open NuGet.Protocol.Core.Types
open NuGet.Repositories
open NuGet.Client
open NuGet.Common
open NuGet.Protocol
open NuGet.Versioning


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

    let findFSharpCoreVersion (fcsVersion : string) =
        use cache = new SourceCacheContext()
        let repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json")
        let r = repo.GetResource<PackageMetadataResource>()
        let x = r.GetMetadataAsync(PackageIdentity("FSharp.Compiler.Service", NuGetVersion(fcsVersion)), cache, NullLogger.Instance, CancellationToken.None).Result
        let y = x.DependencySets |> Seq.head
        let core = y.Packages |> Seq.find (fun d -> d.Id = "FSharp.Core")
        core.VersionRange.MinVersion

    let officialNuGet (version : string) =
        let core = findFSharpCoreVersion version
        [
            NuGetReference(fcsPackageName, version, prerelease=false)
            NuGetReference("FSharp.Core", core.OriginalVersion, prerelease=false)
        ]

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
        [NuGetReference(fcsPackageName, version, Uri(sourceDir), prerelease=false)]
       
    let makeReference (version : NuGetFCSVersion) =
        match version with
        | NuGetFCSVersion.Official version -> officialNuGet version
        | NuGetFCSVersion.Local source -> localNuGet source
                
    let makeReferenceList (version : NuGetFCSVersion) : NuGetReferenceList =
        version
        |> makeReference
        |> NuGetReferenceList

let private defaultConfig (versions : NuGetFCSVersion list) (inputFile : string) =
    let baseJob = Job.Dry
    let jobs =
        versions
        |> List.map NuGet.makeReferenceList
        |> List.map baseJob.WithNuGet
        |> List.map (fun j -> j.WithEnvironmentVariable("FcsBenchmarkInput", inputFile))
    
    Console.WriteLine("inputFile: " + inputFile)
    let config = List.fold (fun (config : IConfig) (job : Job) -> config.AddJob(job)) DefaultConfig.Instance jobs
    config.AddExporter(JsonExporter(indentJson = true))

type Args = {
    [<Option("input", Required = true, HelpText = "Input json")>]
    Input : string
    [<Option("official", Required = false, HelpText = "Official version list.")>]
    OfficialVersions : string seq
    [<Option("local", Required = false, HelpText = "Local nuget source list.")>]
    LocalNuGetSourceDirs : string seq
    [<Option("bdnargs", Required = false)>]
    BdnArgs : string
}

[<EntryPoint>]
let main args =
    use parser = new Parser(fun x -> x.IgnoreUnknownArguments <- false)
    let result = parser.ParseArguments<Args>(args)
    match result with
    | :? Parsed<Args> as parsed ->
        let versions = parseVersions parsed.Value.OfficialVersions parsed.Value.LocalNuGetSourceDirs
        let defaultConfig = defaultConfig versions parsed.Value.Input
        let args =
            Microsoft.CodeAnalysis.CommandLineParser.SplitCommandLineIntoArguments(parsed.Value.BdnArgs, false)
            |> Seq.toArray
        let summary = BenchmarkRunner.Run(typeof<FCSBenchmark>, defaultConfig, args)
        0
    | _ -> failwith "Parse error"