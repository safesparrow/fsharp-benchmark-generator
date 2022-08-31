module FCSBenchmark.Runner

open System
open System.Collections.Generic
open System.IO
open System.Threading
open BenchmarkDotNet.Analysers
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Exporters
open BenchmarkDotNet.Exporters.Json
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Loggers
open FSharp.Compiler.CodeAnalysis
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open FCSBenchmark.Common.Dtos
open CommandLine
open NuGet.Packaging.Core
open NuGet.Protocol.Core.Types
open NuGet.Common
open NuGet.Protocol
open NuGet.Versioning
open OpenTelemetry
open OpenTelemetry.Resources




[<MemoryDiagnoser>]
type Benchmark () =    
    
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
        let inputFile = Environment.GetEnvironmentVariable(Benchmark.InputEnvironmentVariable)
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

[<RequireQualifiedAccess>]
type NuGetFCSVersion =
    | Official of version : string
    | Local of sourceDir : string
    
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

type RunnerArgs =
    {
        [<Option("input", Required = true, HelpText = "Input json")>]
        Input : string
        [<Option("official", Required = false, HelpText = "List of official NuGet versions of FCS to test")>]
        OfficialVersions : string seq
        [<Option("local", Required = false, HelpText = "List of local NuGet source directories to use as sources of FCS dll to test")>]
        LocalNuGetSourceDirs : string seq
        [<Option("iterations", Required = false, Default = 1, HelpText = "Number of iterations - BDN's '--iteration-count'")>]
        Iterations : int
        [<Option("warmups", Required = false, Default = 1, HelpText = "Number of warmups")>]
        Warmups : int
        [<Option("artifacts-path", Required = false, HelpText = "BDN Artifacts output path")>]
        ArtifactsPath : string
    }

let private makeConfig (versions : NuGetFCSVersion list) (args : RunnerArgs) : IConfig =
    let baseJob =
        Job.ShortRun
            .WithWarmupCount(args.Warmups)
            .WithIterationCount(args.Iterations)
    let jobs =
        versions
        |> List.mapi (
            fun i v ->
                let job =
                    baseJob
                        .WithNuGet(NuGet.makeReferenceList v)
                        .WithEnvironmentVariable(Benchmark.InputEnvironmentVariable, args.Input)
                        .WithId(match v with NuGetFCSVersion.Official v -> v | NuGetFCSVersion.Local source -> source)
                job
        )
    
    let d = DefaultConfig.Instance
    let defaultArtifactsPath = Path.Combine(Environment.CurrentDirectory, "FCSBenchmark.Artifacts")
    let artifactsPath =
        args.ArtifactsPath
        |> Option.ofObj
        |> Option.defaultValue defaultArtifactsPath
    let config =
        ManualConfig.CreateEmpty().WithArtifactsPath(artifactsPath)
    let config = config.AddLogger(BenchmarkDotNet.Loggers.NullLogger.Instance)
    let config = config.AddExporter(d.GetExporters() |> Seq.toArray)
    let config = config.AddAnalyser(d.GetAnalysers() |> Seq.toArray)
    let config = config.AddColumnProvider(d.GetColumnProviders() |> Seq.toArray)
    let config = config.AddDiagnoser(d.GetDiagnosers() |> Seq.toArray)
    let config = config.AddValidator(d.GetValidators() |> Seq.toArray)
    let config = config.AddExporter(JsonExporter(indentJson = true))
    let config = List.fold (fun (config : IConfig) (job : Job) -> config.AddJob(job)) config jobs
    config

let parseVersions (officialVersions : string seq) (localNuGetSourceDirs : string seq) =
    let official = officialVersions |> Seq.map NuGetFCSVersion.Official
    let local = localNuGetSourceDirs |> Seq.map NuGetFCSVersion.Local
    Seq.append official local
    |> Seq.toList

open FSharp.Compiler.Diagnostics.Activity
open OpenTelemetry.Trace

[<EntryPoint>]
let main args =
    use parser = new Parser(fun x -> x.IgnoreUnknownArguments <- false)
    let result = parser.ParseArguments<RunnerArgs>(args)
    match result with
    | :? Parsed<RunnerArgs> as parsed ->
        
        let b = Benchmark()
        Environment.SetEnvironmentVariable(Benchmark.InputEnvironmentVariable, parsed.Value.Input)
        
            
        // eventually this would need to only export to the OLTP collector, and even then only if configured. always-on is no good.
        // when this configuration becomes opt-in, we'll also need to safely check activities around every StartActivity call, because those could
        // be null
        use tracerProvider =
            Sdk.CreateTracerProviderBuilder()
               .AddSource(activitySourceName)
               .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName ="program", serviceVersion = "42.42.42.44"))
               .AddOtlpExporter()
               .AddZipkinExporter()
               .Build();
        use mainActivity = activitySource.StartActivity("main")

        let forceCleanup() =
            mainActivity.Dispose()
            activitySource.Dispose()
            tracerProvider.Dispose()
        
        b.Setup()
        b.Run()
        forceCleanup()
        
        b.Setup()
        b.Run()
        0
        
        // let versions =
        //     parseVersions parsed.Value.OfficialVersions parsed.Value.LocalNuGetSourceDirs
        //     |> function
        //         | [] -> failwith "At least one version must be specified"
        //         | versions -> versions
        //
        // let defaultConfig = makeConfig versions parsed.Value
        // let summary = BenchmarkRunner.Run(typeof<Benchmark>, defaultConfig)
        // let analyser = summary.BenchmarksCases[0].Config.GetCompositeAnalyser()
        // let conclusions = List<Conclusion>(analyser.Analyse(summary))
        // MarkdownExporter.Console.ExportToLog(summary, ConsoleLogger.Default)
        // ConclusionHelper.Print(ConsoleLogger.Ascii, conclusions)
        // printfn $"Full Log available in '{summary.LogFilePath}'"
        // printfn $"Reports available in '{summary.ResultsDirectoryPath}'"
        // 0
    | _ -> failwith "Parse error"