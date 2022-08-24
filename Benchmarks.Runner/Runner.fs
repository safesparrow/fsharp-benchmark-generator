﻿module Benchmarks.Runner

open System
open System.Collections.Generic
open System.IO
open System.Threading
open BenchmarkDotNet.Analysers
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Engines
open BenchmarkDotNet.Exporters
open BenchmarkDotNet.Exporters.Json
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Loggers
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
        
    static member InputEnvironmentVariable = "FcsBenchmarkInput"
        
    [<GlobalSetup>]
    member _.Setup() =
        let inputFile = Environment.GetEnvironmentVariable(FCSBenchmark.InputEnvironmentVariable)
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

module NuGet =

    let private fcsPackageName = "FSharp.Compiler.Service"
    let private fsharpCorePackageName = "FSharp.Core"

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

    let extractNuPkgVersion (packageName : string) (file : string) : string option =
        match System.Text.RegularExpressions.Regex.Match(file.ToLowerInvariant(), $".*{packageName.ToLowerInvariant()}.([0-9_\-\.]+).nupkg") with
        | res when res.Success -> res.Groups[1].Value |> Some
        | _ -> None
        
    let inferLocalNuGetVersion (sourceDir : string) (packageName : string) =
        let files = Directory.EnumerateFiles(sourceDir, $"{packageName}.*.nupkg") |> Seq.toArray
        let x = files|> Array.choose (extractNuPkgVersion packageName)
        x |> Array.exactlyOne

    let localNuGet (sourceDir : string) =
        let candidateDirs = [sourceDir; Path.Combine(sourceDir, "artifacts", "packages", "Release", "Release")]
        candidateDirs
        |> List.choose (fun sourceDir ->
            try
                [
                    fcsPackageName, inferLocalNuGetVersion sourceDir fcsPackageName
                    fsharpCorePackageName, inferLocalNuGetVersion sourceDir fsharpCorePackageName
                ]
                |> List.map (fun (name, version) -> NuGetReference(name, version, Uri(sourceDir), prerelease=false))
                |> Some
            with _ -> None
        )
        |> function
            | [] ->
                let candidateDirsString =
                    String.Join(Environment.NewLine, candidateDirs |> List.map (fun dir -> $" - {dir}"))
                failwith $"Could not find nupkg files with sourceDir='{sourceDir}.'\n\
                               Attempted the following candidate directories:\n\
                               {candidateDirsString}"
            | head :: _ -> head
       
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
        |> List.map (fun j -> j.WithEnvironmentVariable(FCSBenchmark.InputEnvironmentVariable, inputFile))
    
    let d = DefaultConfig.Instance
    let config = ManualConfig.CreateEmpty()
    let config = List.fold (fun (config : IConfig) (job : Job) -> config.AddJob(job)) config jobs
    let config = config.AddLogger(BenchmarkDotNet.Loggers.NullLogger.Instance)
    let config = config.AddExporter(d.GetExporters() |> Seq.toArray)
    let config = config.AddAnalyser(d.GetAnalysers() |> Seq.toArray)
    let config = config.AddColumnProvider(d.GetColumnProviders() |> Seq.toArray)
    let config = config.AddDiagnoser(d.GetDiagnosers() |> Seq.toArray)
    let config = config.AddValidator(d.GetValidators() |> Seq.toArray)
    let config = config.AddExporter(JsonExporter(indentJson = true))
    config.UnionRule <- ConfigUnionRule.AlwaysUseGlobal
    config

type RunnerArgs =
    {
        [<Option("input", Required = true, HelpText = "Input json")>]
        Input : string
        [<Option("official", Required = false, HelpText = "List of official NuGet versions of FCS to test")>]
        OfficialVersions : string seq
        [<Option("local", Required = false, HelpText = "List of local NuGet source directories to use as sources of FCS dll to test")>]
        LocalNuGetSourceDirs : string seq
        [<Option("bdnargs", Required = false, HelpText = "Extra BDN arguments")>]
        BdnArgs : string
    }

[<EntryPoint>]
let main args =
    use parser = new Parser(fun x -> x.IgnoreUnknownArguments <- false)
    let result = parser.ParseArguments<RunnerArgs>(args)
    match result with
    | :? Parsed<RunnerArgs> as parsed ->
        let versions = parseVersions parsed.Value.OfficialVersions parsed.Value.LocalNuGetSourceDirs
        let defaultConfig = defaultConfig versions parsed.Value.Input
        let args =
            Microsoft.CodeAnalysis.CommandLineParser.SplitCommandLineIntoArguments(parsed.Value.BdnArgs, false)
            |> Seq.toArray
        let summary = BenchmarkRunner.Run(typeof<FCSBenchmark>, defaultConfig, args)
        let analyser = summary.BenchmarksCases[0].Config.GetCompositeAnalyser()
        let conclusions = List<Conclusion>(analyser.Analyse(summary))
        MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default)
        ConclusionHelper.Print(ConsoleLogger.Ascii, conclusions)
        printfn $"Full Log available in '{summary.LogFilePath}'"
        printfn $"Reports available in '{summary.ResultsDirectoryPath}'"
        0
    | _ -> failwith "Parse error"