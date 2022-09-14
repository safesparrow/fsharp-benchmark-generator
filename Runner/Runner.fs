module FCSBenchmark.Runner

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text.RegularExpressions
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
open FSharp.Compiler.Diagnostics
open NuGet.Packaging.Core
open NuGet.Protocol.Core.Types
open NuGet.Common
open NuGet.Protocol
open NuGet.Versioning
open OpenTelemetry
open OpenTelemetry.Resources
open OpenTelemetry.Trace


type DisposableTempDir() =
    let path = Path.GetTempFileName ()
    do File.Delete (path)
    let dir = Directory.CreateDirectory (path)

    interface IDisposable with
        member _.Dispose () =
            if dir.Exists then
                try
                    dir.Delete ()
                with e ->
                    printfn $"Failed to delete temp directory {dir.FullName}"

    member this.Dir = dir

let copyDirContents sourceDir targetDir =
    Directory.EnumerateFiles (sourceDir)
    |> Seq.iter (fun sourceFile -> File.Copy (sourceFile, Path.Combine (targetDir, Path.GetFileName (sourceFile))))

let time = ((DateTimeOffset) DateTime.Now).ToUnixTimeSeconds ()
let mutable nugetCounter = 1

let createHackedNugetSourceDir (dir : string) =
    let copy = new DisposableTempDir ()
    copyDirContents dir copy.Dir.FullName

    let nupkg =
        copy.Dir.EnumerateFiles ($"FSharp.Compiler.Service.*.nupkg") |> Seq.exactlyOne

    use c = new DisposableTempDir ()
    ZipFile.ExtractToDirectory (nupkg.FullName, c.Dir.FullName)
    let nuspec = Path.Combine (c.Dir.FullName, "FSharp.Compiler.Service.nuspec")
    let text = File.ReadAllText (nuspec)
    let newVersion = $"1000.{time}.{nugetCounter}"

    let replaced =
        Regex.Replace (text, "<version>.+</version>", $"<version>{newVersion}</version>")

    nugetCounter <- nugetCounter + 1
    File.WriteAllText (nuspec, replaced)
    nupkg.Delete ()
    ZipFile.CreateFromDirectory (c.Dir.FullName, nupkg.FullName)
    printfn $"Hacked NuGet source dir from {dir} to {copy.Dir.FullName}"
    copy.Dir, newVersion


[<MemoryDiagnoser>]
type Benchmark() =

    let printDiagnostics (results : FSharpCheckFileResults) =
        match results.Diagnostics with
        | [||] -> ()
        | diagnostics ->
            printfn $"{diagnostics.Length} issues/diagnostics found:"

            for d in diagnostics do
                printfn $"- {d.Message}"

    let cleanCaches (checker : FSharpChecker) =
        checker.InvalidateAll ()
        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients ()

    let performAction (checker : FSharpChecker) (action : BenchmarkAction) =
        match action with
        | AnalyseFile x ->
            [ 1 .. x.Repeat ]
            |> List.iter (fun _ ->
                let result, answer =
                    checker.ParseAndCheckFileInProject (
                        x.FileName,
                        x.FileVersion,
                        FSharp.Compiler.Text.SourceText.ofString x.SourceText,
                        x.Options
                    )
                    |> Async.RunSynchronously

                match answer with
                | FSharpCheckFileAnswer.Aborted -> failwith "checker aborted"
                | FSharpCheckFileAnswer.Succeeded results ->
                    printDiagnostics results
                    cleanCaches checker
            )
        | BuildProject x ->
            [ 1 .. x.Repeat ]
            |> List.iter (fun _ ->
                let diagnostics, exitCode = checker.Compile x.Args |> Async.RunSynchronously

                if exitCode = 0 then
                    cleanCaches checker
                else
                    for d in diagnostics do
                        printfn $"- {d.Message}"

                    failwith "compilation failed"
            )

    let mutable setup : (FSharpChecker * BenchmarkAction list) option = None
    let mutable tracerProvider : TracerProvider option = None

    static member InputEnvironmentVariable = "FcsBenchmarkInput"
    static member OtelEnvironmentVariable = "FcsBenchmarkRecordOtelJaeger"

    static member BenchmarkParallelProjectsAnalysisEnvironmentVariable =
        "FCS_PARALLEL_PROJECTS_ANALYSIS"

    member _.SetupTelemetry () =
        let useTracing =
            match
                Environment.GetEnvironmentVariable (Benchmark.OtelEnvironmentVariable)
                |> bool.TryParse
            with
            | true, useTelemetry -> useTelemetry
            | false, _ -> false

        tracerProvider <-
            if useTracing then
                Sdk
                    .CreateTracerProviderBuilder()
                    .AddSource("fsc")
                    .SetResourceBuilder(
                        ResourceBuilder
                            .CreateDefault()
                            .AddService (serviceName = "program", serviceVersion = "42.42.42.44")
                    )
                    .AddJaegerExporter(fun c ->
                        c.BatchExportProcessorOptions.MaxQueueSize <- 10000000
                        c.BatchExportProcessorOptions.MaxExportBatchSize <- 10000000
                        c.ExportProcessorType <- ExportProcessorType.Simple
                        c.MaxPayloadSizeInBytes <- Nullable (1000000000)
                    )
                    .Build ()
                |> Some
            else
                None

    static member ReadInput (inputFile : string) =
        if File.Exists (inputFile) then
            printfn $"Deserializing inputs from '{inputFile}'"
            let json = File.ReadAllText (inputFile)
            deserializeInputs json
        else
            failwith $"Input file '{inputFile}' does not exist"

    [<GlobalSetup>]
    member this.Setup () =
        let inputFile =
            Environment.GetEnvironmentVariable (Benchmark.InputEnvironmentVariable)

        if File.Exists (inputFile) then
            let inputs = Benchmark.ReadInput (inputFile)

            let checker =
                FSharpChecker.Create (projectCacheSize = inputs.Config.ProjectCacheSize)

            setup <- (checker, inputs.Actions) |> Some
        else
            failwith $"Input file '{inputFile}' does not exist"

        this.SetupTelemetry ()

    [<Benchmark>]
    member _.Run () =
        match setup with
        | None -> failwith "Setup did not run"
        | Some (checker, actions) ->
            for action in actions do
                performAction checker action |> ignore

    [<IterationCleanup>]
    member _.Cleanup () =
        setup |> Option.iter (fun (checker, _) -> cleanCaches checker)

        match tracerProvider with
        | Some tracerProvider -> tracerProvider.ForceFlush () |> ignore
        | None -> ()

    [<GlobalCleanup>]
    member _.GlobalCleanup () =
        tracerProvider |> Option.iter (fun prov -> prov.Dispose ())

[<RequireQualifiedAccess>]
type NuGetFCSVersion =
    | Official of version : string
    | Local of sourceDir : string

module NuGet =

    let private fcsPackageName = "FSharp.Compiler.Service"
    let private fsharpCorePackageName = "FSharp.Core"

    let findFSharpCoreVersion (fcsVersion : string) =
        use cache = new SourceCacheContext ()
        let repo = Repository.Factory.GetCoreV3 ("https://api.nuget.org/v3/index.json")
        let r = repo.GetResource<PackageMetadataResource> ()

        let x =
            r
                .GetMetadataAsync(
                    PackageIdentity ("FSharp.Compiler.Service", NuGetVersion (fcsVersion)),
                    cache,
                    NullLogger.Instance,
                    CancellationToken.None
                )
                .Result

        let y = x.DependencySets |> Seq.head
        let core = y.Packages |> Seq.find (fun d -> d.Id = "FSharp.Core")
        core.VersionRange.MinVersion

    let officialNuGet (version : string) =
        let core = findFSharpCoreVersion version

        [
            NuGetReference (fcsPackageName, version, prerelease = false)
            NuGetReference ("FSharp.Core", core.OriginalVersion, prerelease = false)
        ]

    let extractNuPkgVersion (packageName : string) (file : string) : string option =
        match
            System.Text.RegularExpressions.Regex.Match (
                file.ToLowerInvariant (),
                $".*{packageName.ToLowerInvariant ()}.([0-9_\-\.]+).nupkg"
            )
        with
        | res when res.Success -> res.Groups[1].Value |> Some
        | _ -> None

    let inferLocalNuGetVersion (sourceDir : string) (packageName : string) =
        let files =
            Directory.EnumerateFiles (sourceDir, $"{packageName}.*.nupkg") |> Seq.toArray

        let x = files |> Array.choose (extractNuPkgVersion packageName)
        x |> Array.exactlyOne

    let localNuGet (sourceDir : string) =
        let candidateDirs =
            [
                sourceDir
                Path.Combine (sourceDir, "artifacts", "packages", "Release", "Release")
            ]

        candidateDirs
        |> List.choose (fun sourceDir ->
            try
                [
                    fcsPackageName, inferLocalNuGetVersion sourceDir fcsPackageName
                    fsharpCorePackageName, inferLocalNuGetVersion sourceDir fsharpCorePackageName
                ]
                |> List.map (fun (name, version) -> NuGetReference (name, version, Uri (sourceDir), prerelease = false))
                |> Some
            with _ ->
                None
        )
        |> function
            | [] ->
                let candidateDirsString =
                    String.Join (Environment.NewLine, candidateDirs |> List.map (fun dir -> $" - {dir}"))

                failwith
                    $"Could not find nupkg files with sourceDir='{sourceDir}.'\n\
                               Attempted the following candidate directories:\n\
                               {candidateDirsString}"
            | head :: _ ->
                let source = head[0].PackageSource.LocalPath
                let source, version = createHackedNugetSourceDir source

                head
                |> List.map (fun ref ->
                    let version =
                        match ref.PackageName with
                        | "FSharp.Compiler.Service" -> version
                        | _ -> ref.PackageVersion

                    NuGetReference (ref.PackageName, version, Uri (source.FullName), ref.Prerelease)
                )

    let makeReference (version : NuGetFCSVersion) =
        match version with
        | NuGetFCSVersion.Official version -> officialNuGet version
        | NuGetFCSVersion.Local source -> localNuGet source

    let makeReferenceList (version : NuGetFCSVersion) : NuGetReferenceList =
        version |> makeReference |> NuGetReferenceList

type RunnerArgs =
    {
        [<Option("input", Required = true, HelpText = "Input json. Accepts multiple values.")>]
        Input : string seq
        [<Option("official", Required = false, HelpText = "List of official NuGet versions of FCS to test")>]
        OfficialVersions : string seq
        [<Option("local",
                 Required = false,
                 HelpText = "List of local NuGet source directories to use as sources of FCS dll to test")>]
        LocalNuGetSourceDirs : string seq
        [<Option("iterations",
                 Required = false,
                 Default = 1,
                 HelpText = "Number of iterations - BDN's '--iteration-count'")>]
        Iterations : int
        [<Option("warmups", Required = false, Default = 1, HelpText = "Number of warmups")>]
        Warmups : int
        [<Option("artifacts-path", Required = false, HelpText = "BDN Artifacts output path")>]
        ArtifactsPath : string
        [<Option("record-otel-jaeger",
                 Required = false,
                 Default = false,
                 HelpText =
                     "If enabled, records and sends OpenTelemetry trace to a localhost Jaeger instance using the default port")>]
        RecordOtelJaeger : bool
        [<Option("parallel-analysis",
                 Default = ParallelAnalysisMode.Off,
                 Required = false,
                 HelpText =
                     "Off = parallel analysis off, On = parallel analysis on, Compare = runs two benchmarks with parallel analysis on and off")>]
        ParallelAnalysis : ParallelAnalysisMode
        [<Option("gc",
                 Default = GCMode.Workstation,
                 Required = false,
                 HelpText = "Whether to use 'workstation' or 'server' GC, or 'compare'.")>]
        GCMode : GCMode
    }

let private makeConfig (versions : NuGetFCSVersion list) (args : RunnerArgs) : IConfig =
    let baseJob =
        Job.Dry.WithWarmupCount(args.Warmups).WithIterationCount (args.Iterations)

    let inputs = args.Input |> Seq.toList |> List.mapi (fun i x -> i, x)

    let parallelAnalysisModes =
        match args.ParallelAnalysis with
        | ParallelAnalysisMode.Off -> [ false ]
        | ParallelAnalysisMode.On -> [ true ]
        | ParallelAnalysisMode.Compare -> [ true ; false ]
        | unknown -> failwith $"Unrecognised value of 'parallelAnalysisMode': {unknown}"

    let gcModes =
        match args.GCMode with
        | GCMode.Workstation -> [ GCMode.Workstation ]
        | GCMode.Server -> [ GCMode.Server ]
        | GCMode.Compare -> [ GCMode.Server ; GCMode.Workstation ]
        | unknown -> failwith $"Unrecognised value of 'gcMode': {unknown}"

    let versionsSources =
        versions
        |> List.map (fun version ->
            let versionName =
                match version with
                | NuGetFCSVersion.Official v -> v
                | NuGetFCSVersion.Local source -> source

            let refs = NuGet.makeReferenceList version
            versionName, refs
        )

    let combinations =
        List.allPairs (List.allPairs (List.allPairs inputs versionsSources) parallelAnalysisModes) gcModes

    let jobs =
        combinations
        |> List.mapi (fun i ((((inputIdx, input), (versionName, refs)), parallelAnalysisMode), gcMode) ->
            let useServerGc = gcMode = GCMode.Server

            let jobName =
                $"fcs={versionName}_input=#{inputIdx}_parallel={parallelAnalysisMode}_serverGc={useServerGc}"

            let job =
                baseJob
                    .WithNuGet(refs)
                    .WithEnvironmentVariable(Benchmark.InputEnvironmentVariable, input)
                    .WithEnvironmentVariable(
                        Benchmark.BenchmarkParallelProjectsAnalysisEnvironmentVariable,
                        parallelAnalysisMode.ToString ()
                    )
                    .WithEnvironmentVariable(
                        Benchmark.OtelEnvironmentVariable,
                        if args.RecordOtelJaeger then "true" else "false"
                    )
                    .WithGcServer(useServerGc)
                    .WithId (jobName)

            job
        )

    let d = DefaultConfig.Instance

    let defaultArtifactsPath =
        Path.Combine (Environment.CurrentDirectory, "FCSBenchmark.Artifacts")

    let artifactsPath =
        args.ArtifactsPath |> Option.ofObj |> Option.defaultValue defaultArtifactsPath

    let config = ManualConfig.CreateEmpty().WithArtifactsPath (artifactsPath)
    let config = config.AddLogger (BenchmarkDotNet.Loggers.NullLogger.Instance)
    let config = config.AddExporter (d.GetExporters () |> Seq.toArray)
    let config = config.AddAnalyser (d.GetAnalysers () |> Seq.toArray)
    let config = config.AddColumnProvider (d.GetColumnProviders () |> Seq.toArray)
    let config = config.AddDiagnoser (d.GetDiagnosers () |> Seq.toArray)
    let config = config.AddValidator (d.GetValidators () |> Seq.toArray)
    let config = config.AddExporter (JsonExporter (indentJson = true))

    let config =
        List.fold (fun (config : IConfig) (job : Job) -> config.AddJob (job)) config jobs

    config

let parseVersions (officialVersions : string seq) (localNuGetSourceDirs : string seq) =
    let official = officialVersions |> Seq.map NuGetFCSVersion.Official
    let local = localNuGetSourceDirs |> Seq.map NuGetFCSVersion.Local
    Seq.append official local |> Seq.toList

let runStandard args =
    let versions =
        parseVersions args.OfficialVersions args.LocalNuGetSourceDirs
        |> function
            | [] -> failwith "At least one version must be specified"
            | versions -> versions

    let defaultConfig = makeConfig versions args
    let summary = BenchmarkRunner.Run (typeof<Benchmark>, defaultConfig)
    let analyser = summary.BenchmarksCases[ 0 ].Config.GetCompositeAnalyser ()
    let conclusions = List<Conclusion> (analyser.Analyse (summary))
    MarkdownExporter.Console.ExportToLog (summary, ConsoleLogger.Default)
    ConclusionHelper.Print (ConsoleLogger.Ascii, conclusions)
    printfn $"Full Log available in '{summary.LogFilePath}'"
    printfn $"Reports available in '{summary.ResultsDirectoryPath}'"
    0

[<EntryPoint>]
let main args =
    use parser = new Parser (fun x -> x.IgnoreUnknownArguments <- false)
    let result = parser.ParseArguments<RunnerArgs> (args)

    match result with
    | :? Parsed<RunnerArgs> as parsed -> runStandard parsed.Value
    | :? NotParsed<RunnerArgs> as notParsed ->
        let errorsString =
            notParsed.Errors
            |> Seq.map (fun e -> e.ToString ())
            |> fun lines -> String.Join (Environment.NewLine, lines)

        failwith $"Parse errors: {errorsString}"
    | _ -> failwith "Unexpected result type"
