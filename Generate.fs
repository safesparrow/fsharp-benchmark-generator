module FCSBenchmark.Generator.Generate

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Security.Cryptography
open System.Text.RegularExpressions
open FCSBenchmark.Common.Dtos
open CommandLine
open FCSBenchmark.Generator.FCSCheckouts
open FCSBenchmark.Generator.RepoSetup
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open Ionide.ProjInfo.Types
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Serilog
open Serilog.Context
open Serilog.Events

[<CLIMutable>]
type CheckAction =
    {
        FileName : string
        ProjectName : string
        [<JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)>]
        [<System.ComponentModel.DefaultValue(1)>]
        Repeat : int
    }

[<CLIMutable>]
type CodebasePrepStep = { Command : string ; Args : string }

[<CLIMutable>]
type BenchmarkCase =
    {
        Repo : RepoSetup.RepoSpec
        LocalCodeRoot : string
        CodebasePrep : CodebasePrepStep list
        SlnRelative : string
        CheckActions : CheckAction list
    }

[<MethodImpl(MethodImplOptions.NoInlining)>]
let init (slnPath : string) =
    let directoryName = Path.GetDirectoryName slnPath
    log.Verbose ("Calling {method} for directory {directory}", "Ionide.ProjInfo.Init.init", directoryName)
    Init.init (DirectoryInfo (directoryName)) None

type Codebase =
    | Local of string
    | Git of LibGit2Sharp.Repository

    member this.Path =
        match this with
        | Local codeRoot -> codeRoot
        | Git repo -> repo.Info.WorkingDirectory

let prepareCodebase (config : Config) (case : BenchmarkCase) : Codebase =
    use _ = LogContext.PushProperty ("step", "PrepareCodebase")

    let codebase =
        match (case.Repo :> obj, case.LocalCodeRoot) with
        | null, null -> failwith "Either git repo or local code root details are required"
        | _, null ->
            let repo = prepareRepo config case.Repo
            Codebase.Git repo
        | null, codeRoot -> Codebase.Local codeRoot
        | _, _ -> failwith $"Both git repo and local code root were provided - that's not supported"

    log.Information ("Running {steps} codebase prep steps", case.CodebasePrep.Length)

    case.CodebasePrep
    |> List.iteri (fun i step ->
        log.Verbose ("Running codebase prep step {step}/{steps}", i + 1, case.CodebasePrep.Length)
        Utils.runProcess step.Command step.Args codebase.Path [] LogEventLevel.Verbose
    )

    codebase

let private withRedirectedConsole<'a> (f : unit -> 'a) =
    let originalOut = Console.Out
    let originalError = Console.Error
    use out = new StringWriter ()
    use error = new StringWriter ()
    Console.SetOut (out)
    Console.SetError (error)
    let res = f ()
    Console.SetOut (originalOut)
    Console.SetOut (originalError)
    res, (out.ToString (), error.ToString ())

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private doLoadOptions (toolsPath : ToolsPath) (sln : string) =
    // TODO allow customization of build properties
    let props = []
    let loader = WorkspaceLoader.Create (toolsPath, props)
    let _ = Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults ()

    let projects, _ =
        fun () -> loader.LoadSln (sln, [], BinaryLogGeneration.Off) |> Seq.toList
        |> withRedirectedConsole

    log.Information ("{projectsCount} projects loaded from {sln}", projects.Length, sln)

    if projects.Length = 0 then
        failwith $"No projects were loaded from {sln} - this indicates an error in cracking the projects"

    let fsOptions = FCS.mapManyOptions projects |> Seq.toList

    fsOptions
    |> List.zip projects
    |> List.map (fun (project, fsOptions) ->
        let name = Path.GetFileNameWithoutExtension (project.ProjectFileName)
        name, fsOptions
    )
    |> dict

/// Not a bulletproof way to get an absolute path from a possibly relative one
let toAbsolutePath (baseDir : string) (path : string) =
    if Path.IsPathRooted (path) then
        path
    else
        Path.Combine (baseDir, path)

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private loadOptions (sln : string) =
    use _ = LogContext.PushProperty ("step", "LoadOptions")
    log.Verbose ("Constructing FSharpProjectOptions from {sln}", sln)
    let toolsPath = init sln
    doLoadOptions toolsPath sln

let private generateInputs (case : BenchmarkCase) (codeRoot : string) =
    let sln = Path.Combine (codeRoot, case.SlnRelative)
    let options = loadOptions sln

    log.Verbose ("Generating actions")

    let actions =
        case.CheckActions
        |> List.mapi (fun i {
                                FileName = projectRelativeFileName
                                ProjectName = projectName
                                Repeat = repeat
                            } ->
            let project = options[projectName]

            let filePath =
                Path.Combine (Path.GetDirectoryName (project.ProjectFileName), projectRelativeFileName)

            let fileText = File.ReadAllText (filePath)

            BenchmarkAction.AnalyseFile
                {
                    FileName = filePath
                    FileVersion = i
                    SourceText = fileText
                    Options = project
                    Repeat = repeat
                }
        )

    let config : BenchmarkConfig =
        {
            BenchmarkConfig.ProjectCacheSize = 200
        }

    {
        BenchmarkInputs.Actions = actions
        BenchmarkInputs.Config = config
    }

let private makeInputsPath (baseDir : string) =
    let dateStr = DateTime.UtcNow.ToString ("yyyy-MM-dd_HH-mm-ss")
    Path.Combine (baseDir, $"{dateStr}.fcsinputs.json")

// These are the env variables that Ionide.ProjInfo seems to set (in-process).
// We need to get rid of them so that the child 'dotnet run' process is using the right tools
let private projInfoEnvVariables =
    [
        "MSBuildExtensionsPath"
        "DOTNET_ROOT"
        "MSBUILD_EXE_PATH"
        "DOTNET_HOST_PATH"
        "MSBuildSDKsPath"
    ]

let private emptyProjInfoEnvironmentVariables () =
    projInfoEnvVariables |> List.map (fun var -> var, "")

let private resultsJsonPath (bdnArtifactDir : string) (benchmarkName : string) =
    Path.Combine (bdnArtifactDir, $@"results/{benchmarkName}-report.json")

type RunResultSummary =
    {
        MeanS : double
        AllocatedMB : double
    }

let extractResultsFromJson (summary : JObject) : RunResultSummary =
    let benchmark = summary["Benchmarks"][0]
    let stats = benchmark["Statistics"]
    let meanMicros = stats[ "Mean" ].ToString () |> Double.Parse

    let metrics = benchmark["Metrics"] :?> JArray

    let allocatedBytes =
        let found =
            metrics
            |> Seq.find (fun m -> (m["Descriptor"]["Id"]).ToString () = "Allocated Memory")

        found[ "Value" ].ToString () |> Double.Parse

    {
        MeanS = Math.Round (meanMicros / 1000000000.0, 3)
        AllocatedMB = Math.Round (allocatedBytes / 1024.0 / 1024.0, 3)
    }

let private readBasicJsonResults (bdnArtifactsDir : string) (benchmarkClass : string) =
    let json = File.ReadAllText (resultsJsonPath bdnArtifactsDir benchmarkClass)
    let jObject = JsonConvert.DeserializeObject<JObject> (json)
    extractResultsFromJson jObject

let private readJsonResultsSummary (bdnArtifactsDir : string) (benchmarkClass : string) =
    let jsonResultsPath = resultsJsonPath bdnArtifactsDir benchmarkClass
    let json = File.ReadAllText (jsonResultsPath)
    let jObject = JsonConvert.DeserializeObject<JObject> (json)
    extractResultsFromJson jObject

let copyDir sourceDir targetDir =
    Directory.CreateDirectory (targetDir) |> ignore

    Directory.EnumerateFiles (sourceDir)
    |> Seq.iter (fun sourceFile -> File.Copy (sourceFile, Path.Combine (targetDir, Path.GetFileName (sourceFile))))

let rec copyRunnerProjectFilesToTemp (sourceDir : string) =
    let buildDir =
        let file = Path.GetTempFileName ()
        File.Delete (file)
        Path.Combine (Path.GetDirectoryName (file), Path.GetFileNameWithoutExtension (file))

    try
        Directory.CreateDirectory (buildDir) |> ignore

        for dirName in [ "Runner" ; "Serialisation" ; "BDNExtensions" ] do
            let sourceDir = Path.Combine (sourceDir, dirName)
            let targetDir = Path.Combine (buildDir, dirName)
            Directory.CreateDirectory (targetDir) |> ignore

            Directory.EnumerateFiles (sourceDir)
            |> Seq.iter (fun sourceFile ->
                File.Copy (sourceFile, Path.Combine (targetDir, Path.GetFileName (sourceFile)))
            )

        Path.Combine (buildDir, "Runner")
    with _ ->
        Directory.Delete (buildDir, recursive = true)
        reraise ()

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
                    log.Warning ("Failed to delete temp directory {dir}.", dir.FullName)

    member this.Dir = dir

let inputsBaseDir (config : Config) =
    Path.Combine(config.BaseDir, "__inputs") 

let md5 (file : string) =
    use md5 = MD5.Create()
    use stream = File.OpenRead(file)
    let bytes = md5.ComputeHash(stream)
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

let inputsHashPath (inputsPath : string) =
    Path.Combine(Path.GetDirectoryName(inputsPath), Path.GetFileNameWithoutExtension(inputsPath) + ".hash")

let buildInputsDict (config : Config) =
    let dir = inputsBaseDir config
    Directory.EnumerateFiles(dir, "*.fcsinputs.json")
    |> Seq.choose (fun f ->
        let hashPath = inputsHashPath f
        if File.Exists hashPath then
            let hash = File.ReadAllText(hashPath)
            (hash, f) |> Some
        else
            None
    )
    |> dict

let getOrGenerateInputs (config : Config) (casePath : string) (case : BenchmarkCase) absoluteCodebasePath =
    let inputsBaseDir = inputsBaseDir config
    Directory.CreateDirectory inputsBaseDir |> ignore
    let cache = buildInputsDict config
    let caseMD5 = md5 casePath
    log.Information("Hash: {hash}", caseMD5)
    match cache.TryGetValue caseMD5 with
    | true, cachedInputsPath ->
        log.Information("Using cached generated inputs in {path}", cachedInputsPath)
        cachedInputsPath
    | false, _ ->
    
    let inputs = generateInputs case absoluteCodebasePath
    let inputsPath = makeInputsPath inputsBaseDir
    log.Information ("Serializing inputs as {inputsPath}", inputsPath)
    let serialized = serializeInputs inputs
    File.WriteAllText (inputsPath, serialized)
    let hashPath = inputsHashPath inputsPath
    log.Information ("Serializing hash {hash} as {hashPath}", caseMD5, hashPath)
    Directory.CreateDirectory (Path.GetDirectoryName hashPath) |> ignore
    File.WriteAllText(hashPath, caseMD5)
    inputsPath

let private prepareAndRun
    (config : Config)
    (casePath : string, case : BenchmarkCase)
    (dryRun : bool)
    (cleanup : bool)
    (iterations : int)
    (warmups : int)
    (recordOtelJaeger : bool)
    (parallelAnalysisMode : ParallelAnalysisMode)
    (gcMode : GCMode)
    (versions : NuGetFCSVersion list)
    =
    let codebase = prepareCodebase config case

    let binDir =
        Path.GetDirectoryName (Assembly.GetAssembly(typeof<BenchmarkCase>).Location)

    let absoluteCodebasePath = toAbsolutePath binDir codebase.Path
    let inputsPath = getOrGenerateInputs config casePath case absoluteCodebasePath

    if dryRun = false then
        use _ = LogContext.PushProperty ("step", "Run")

        let workingDir =
            Path.GetDirectoryName (Assembly.GetAssembly(typeof<RepoSpec>).Location)

        let workingDir = copyRunnerProjectFilesToTemp workingDir
        use nugetPackagesDir = new DisposableTempDir ()

        let extraEnvVariables =
            [
                // Clear variables set by 'dotnet' that affect the runner
                "DOTNET_ROOT_X64", ""
            ]

        let envVariables = emptyProjInfoEnvironmentVariables () @ extraEnvVariables

        let bdnArtifactsDir =
            Path.Combine (Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts")

        let exe = "dotnet"

        let versionsArgs =
            let o =
                versions
                |> List.choose (
                    function
                    | NuGetFCSVersion.Official v -> Some v
                    | _ -> None
                )
                |> function
                    | [] -> ""
                    | officials -> "--official " + String.Join (" ", officials)

            let l =
                versions
                |> List.choose (
                    function
                    | NuGetFCSVersion.Local v -> Some v
                    | _ -> None
                )
                |> function
                    | [] -> ""
                    | locals ->
                        "--local "
                        + String.Join (" ", locals |> List.map (fun local -> local.TrimEnd ([| '\\' ; '/' |])))

            o + " " + l

        let otelStr = if recordOtelJaeger then "--record-otel-jaeger" else ""
        let parallelAnalysisStr = $"--parallel-analysis={parallelAnalysisMode}"
        let gcModeStr = $"--gc={gcMode}"

        let artifactsPath =
            Path.Combine (Environment.CurrentDirectory, "FCSBenchmark.Artifacts")

        let args =
            $"run -c Release -- --artifacts-path={artifactsPath} --input={inputsPath} --iterations={iterations} --warmups={warmups} {otelStr} {parallelAnalysisStr} {gcModeStr} {versionsArgs}"
                .Trim ()

        let env =
            envVariables
            |> List.filter (fun (key, value) -> String.IsNullOrEmpty (value) = false)
            |> List.map (fun (key, value) -> $"{key}={value}")
            |> fun x -> String.Join (" ", x)

        log.Information (
            "Starting the benchmark:\n\
             - Full BDN output can be found in {artifactFiles}.\n\
             - Full commandline: '{exe} {args}'\n\
             - Working directory: '{dir}'.",
            $"{bdnArtifactsDir}/*.log\n\
             - Extra environment variables: {env}",
            exe,
            args,
            workingDir
        )

        Utils.runProcess exe args workingDir envVariables LogEventLevel.Information
    else
        log.Information ("Not running the benchmark as requested")

    match codebase, cleanup with
    | Local _, _ -> ()
    | Git _, false -> ()
    | Git repo, true ->
        log.Information ("Cleaning up checked out git repo {repoPath} as requested", repo.Info.Path)
        Directory.Delete repo.Info.Path

type Args =
    {
        [<CommandLine.Option('c', "checkouts", Default = ".artifacts", HelpText = "Base directory for git checkouts")>]
        CheckoutsDir : string
        [<CommandLine.Option("forceFcsBuild",
                             Default = false,
                             HelpText = "Force build git-sourced FCS versions even if the binaries already exist")>]
        ForceFCSBuild : bool
        [<CommandLine.Option('i', SetName = "input", HelpText = "Path to the input file describing the benchmark.")>]
        Input : string
        [<CommandLine.Option("sample",
                             SetName = "input",
                             HelpText = "Use a predefined sample benchmark with the given name")>]
        SampleInput : string
        [<CommandLine.Option("dry-run",
                             HelpText =
                                 "If set, prepares the benchmark and prints the commandline to run it, then exits")>]
        DryRun : bool
        [<CommandLine.Option(Default = false,
                             HelpText =
                                 "If set, removes the checkout directory afterwards. Doesn't apply to local codebases")>]
        Cleanup : bool
        [<CommandLine.Option('n', "iterations", Default = 1, HelpText = "Number of iterations to run")>]
        Iterations : int
        [<CommandLine.Option('w', "warmups", Default = 1, HelpText = "Number of warmups to run")>]
        Warmups : int
        [<CommandLine.Option('v',
                             "verbose",
                             Default = false,
                             HelpText = "Verbose logging. Includes output of all preparation steps.")>]
        Verbose : bool
        [<Option("official",
                 Required = false,
                 HelpText = "A publicly available FCS NuGet version to test. Supports multiple values.")>]
        OfficialVersions : string seq
        [<Option("local",
                 Required = false,
                 HelpText =
                     "A local NuGet source to use for testing locally-generated FCS nupkg files. Supports multiple values.")>]
        LocalNuGetSourceDirs : string seq
        [<Option("github",
                 Required = false,
                 HelpText =
                     "An FSharp repository&revision, in the form 'owner/repo/revision' eg. 'dotnet/fsharp/5a72e586278150b7aea4881829cd37be872b2043. Supports multiple values.")>]
        GitHubVersions : string seq
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

let readSampleInput (sampleName : string) =
    let assemblyDir =
        Path.GetDirectoryName (Assembly.GetAssembly(typeof<Args>).Location)

    let path = Path.Combine (assemblyDir, "inputs", $"{sampleName}.json")

    if File.Exists (path) then
        path
    else
        let dir = Path.GetDirectoryName (path)

        if Directory.Exists (dir) then
            let samples =
                Directory.EnumerateFiles (dir, "*.json")
                |> Seq.map Path.GetFileNameWithoutExtension

            let samplesString =
                let str = String.Join (", ", samples)
                $"[{str}]"

            failwith $"Sample {path} does not exist. Available samples are: {samplesString}"
        else
            failwith $"Samples directory '{dir}' does not exist"

let prepareCase (args : Args) : string * BenchmarkCase =
    use _ = LogContext.PushProperty ("step", "Read input")

    try
        let path =
            match args.Input |> Option.ofObj, args.SampleInput |> Option.ofObj with
            | None, None -> failwith $"No input specified"
            | Some input, _ -> input
            | None, Some sample -> readSampleInput sample

        log.Verbose ("Read and deserialize inputs from {path}", path)

        path
        |> File.ReadAllText
        |> JsonConvert.DeserializeObject<BenchmarkCase>
        |> fun case ->
            let defaultCodebasePrep =
                [
                    {
                        CodebasePrepStep.Command = "dotnet"
                        CodebasePrepStep.Args =
                            $"msbuild /t:Restore /p:RestoreUseStaticGraphEvaluation=true {case.SlnRelative}"
                    }
                ]

            let codebasePrep =
                match obj.ReferenceEquals (case.CodebasePrep, null) with
                | true -> defaultCodebasePrep
                | false -> case.CodebasePrep

            let case = 
                { case with
                    CodebasePrep = codebasePrep
                }
            path, case
    with e ->
        let msg = $"Failed to read inputs file: {e.Message}"
        log.Fatal (msg)
        reraise ()

type FCSVersionsArgs =
    {
        Official : string list
        Local : string list
        Git : string list
    }

let prepareFCSVersions (config : Config) (raw : FCSVersionsArgs) =
    let official = raw.Official |> List.map NuGetFCSVersion.Official
    let local = raw.Local |> List.map NuGetFCSVersion.Local

    let git =
        raw.Git
        |> List.map (fun specStr ->
            let m =
                Regex.Match (specStr, "^([0-9a-zA-Z_\-]+)/([0-9a-zA-Z_\-]+)/([0-9a-zA-Z_\-]+)$")

            if m.Success then
                let owner, repo, revision = m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value
                let url = $"https://github.com/{owner}/{repo}"
                let spec = FCSRepoSpec.Custom { GitUrl = url ; Revision = revision }
                let checkout = checkoutAndBuild config spec
                NuGetFCSVersion.Local checkout.Dir
            else
                failwith $"Invalid GitHub FCS repo spec: {specStr}. Expected format: 'owner/repo/revision'"
        )

    local @ official @ git
    |> function
        | [] -> failwith "At least one version must be specified"
        | versions -> versions

let run (args : Args) : unit =
    log <-
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo
            .Console(
                outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {step:j}: {Message:lj}{NewLine}{Exception}"
            )
            .MinimumLevel
            .Is(
                if args.Verbose then
                    LogEventLevel.Verbose
                else
                    LogEventLevel.Information
            )
            .CreateLogger ()

    try
        log.Verbose ("CLI args provided:" + Environment.NewLine + "{args}", args)

        let config =
            {
                Config.BaseDir = args.CheckoutsDir
                Config.ForceFCSBuild = args.ForceFCSBuild
            }

        let rawVersions =
            {
                Official = args.OfficialVersions |> Seq.toList
                Local = args.LocalNuGetSourceDirs |> Seq.toList
                Git = args.GitHubVersions |> Seq.toList
            }

        let versions = prepareFCSVersions config rawVersions
        let case = prepareCase args

        use _ = LogContext.PushProperty ("step", "PrepareAndRun")

        prepareAndRun
            config
            case
            args.DryRun
            args.Cleanup
            args.Iterations
            args.Warmups
            args.RecordOtelJaeger
            args.ParallelAnalysis
            args.GCMode
            versions
    with ex ->
        if args.Verbose then
            log.Fatal (ex, "Failure.")
        else
            log.Fatal (ex, "Failure. Consider using --verbose for extra information.")

[<EntryPoint>]
[<MethodImpl(MethodImplOptions.NoInlining)>]
let main args =
    let parseResult = Parser.Default.ParseArguments<Args> args
    parseResult.WithParsed (run) |> ignore
    if parseResult.Tag = ParserResultType.Parsed then 0 else 1
