module Benchmarks.Generator.Generate

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open Benchmarks.Common.Dtos
open CommandLine
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open Ionide.ProjInfo.Types
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Serilog
open Serilog.Context
open Serilog.Events

let mutable private log : ILogger = null

/// General utilities
[<RequireQualifiedAccess>]
module Utils =
    
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
        p.ErrorDataReceived.Add(fun args ->
            log.Information(args.Data)
            log.Error(args.Data)
        )
        p.Start() |> ignore
        p.BeginErrorReadLine()
        p.BeginOutputReadLine()
        p.WaitForExit()
        
        if p.ExitCode <> 0 then
            log.Error("Process '{name} {args}' failed - check full process output above.", name, args)
            failwith $"Process {name} {args} failed - check full process output above."

/// Handling Git operations
[<RequireQualifiedAccess>]
module Git =
    open LibGit2Sharp
    
    let clone (dir : string) (gitUrl : string) : Repository =
        if Directory.Exists dir then
            failwith $"{dir} already exists for code root"
        log.Verbose("Fetching '{gitUrl}' in '{dir}'", gitUrl, dir)
        Repository.Init(dir) |> ignore
        let repo = new Repository(dir)
        let remote = repo.Network.Remotes.Add("origin", gitUrl)
        repo.Network.Fetch(remote.Name, [])
        repo
        
    let checkout (repo : Repository) (revision : string) : unit =
        log.Verbose("Checkout revision {revision} in {repo.Info.Path}", revision, repo.Info.Path)
        Commands.Checkout(repo, revision) |> ignore

/// Preparing a codebase based on a 'RepoSpec'
[<RequireQualifiedAccess>]
module RepoSetup =
    open LibGit2Sharp

    [<CLIMutable>]
    type RepoSpec =
        {
            Name : string
            GitUrl : string
            Revision : string
        }
            with override this.ToString() = $"{this.Name} ({this.GitUrl}) @ {this.Revision}"
        
    type Config =
        {
            BaseDir : string
        }
    
    let revisionDir (config : Config) (spec : RepoSpec) =
        Path.Combine(config.BaseDir, spec.Name, spec.Revision)
    
    let prepare (config : Config) (spec : RepoSpec) =
        log.Information("Preparing repo {spec}", spec)
        let dir = revisionDir config spec
        if Repository.IsValid dir |> not then
            use repo = Git.clone dir spec.GitUrl
            Git.checkout repo spec.Revision
            repo
        else
            log.Information("{dir} already exists - will assume the correct repository is already checked out", dir)
            new Repository(dir)

[<RequireQualifiedAccess>]
module Generate =
    
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
    type CodebasePrepStep =
        {
            Command : string
            Args : string
        }
    
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
        log.Verbose("Calling {method} for directory {directory}", "Ionide.ProjInfo.Init.init", directoryName)
        Init.init (DirectoryInfo(directoryName)) None
    
    type Config =
        {
            CheckoutBaseDir : string
            FcsDllPath : string option // defaults to a NuGet package
            Iterations : int option
        }
    
    type Codebase =
        | Local of string
        | Git of LibGit2Sharp.Repository
        with member this.Path = match this with | Local codeRoot -> codeRoot | Git repo -> repo.Info.WorkingDirectory
    
    let prepareCodebase (config : Config) (case : BenchmarkCase) : Codebase =
        use _ = LogContext.PushProperty("step", "PrepareCodebase")
        let codebase =
            match (case.Repo :> obj, case.LocalCodeRoot) with
            | null, null -> failwith "Either git repo or local code root details are required"
            | _, null ->
                let repo = RepoSetup.prepare {BaseDir = config.CheckoutBaseDir} case.Repo
                Codebase.Git repo
            | null, codeRoot ->
                Codebase.Local codeRoot
            | _, _ -> failwith $"Both git repo and local code root were provided - that's not supported"
        
        log.Information("Running {steps} codebase prep steps", case.CodebasePrep.Length)
        case.CodebasePrep
        |> List.iteri (fun i step ->
            log.Verbose("Running codebase prep step {step}/{steps}", i+1, case.CodebasePrep.Length)
            Utils.runProcess step.Command step.Args codebase.Path [] LogEventLevel.Verbose
        )
        codebase
    
    let private withRedirectedConsole<'a> (f : unit -> 'a) =
        let originalOut = Console.Out
        let originalError = Console.Error
        use out = new StringWriter()
        use error = new StringWriter()
        Console.SetOut(out)
        Console.SetError(error)
        let res = f()
        Console.SetOut(originalOut)
        Console.SetOut(originalError)
        res, (out.ToString(), error.ToString())
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private doLoadOptions (toolsPath : ToolsPath) (sln : string) =
        // TODO allow customization of build properties
        let props = []
        let loader = WorkspaceLoader.Create(toolsPath, props)
        let _ = Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()
        
        let projects, _ =
            fun () -> loader.LoadSln(sln, [], BinaryLogGeneration.Off) |> Seq.toList
            |> withRedirectedConsole
            
        log.Information("{projectsCount} projects loaded from {sln}", projects.Length, sln)
        if projects.Length = 0 then
            failwith $"No projects were loaded from {sln} - this indicates an error in cracking the projects"
        
        let fsOptions = FCS.mapManyOptions projects |> Seq.toList
        
        fsOptions
        |> List.zip projects
        |> List.map (fun (project, fsOptions) ->
            let name = Path.GetFileNameWithoutExtension(project.ProjectFileName)
            name, fsOptions
        )
        |> dict
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private loadOptions (sln : string) =
        use _ = LogContext.PushProperty("step", "LoadOptions")
        log.Verbose("Constructing FSharpProjectOptions from {sln}", sln)
        let toolsPath = init sln
        doLoadOptions toolsPath sln
    
    let private generateInputs (case : BenchmarkCase) (codeRoot : string) =
        let sln = Path.Combine(codeRoot, case.SlnRelative)
        let options = loadOptions sln
        
        log.Verbose("Generating actions")
        let actions =
            case.CheckActions
            |> List.mapi (fun i {FileName = projectRelativeFileName; ProjectName = projectName; Repeat = repeat} ->
                let project = options[projectName]
                let filePath = Path.Combine(Path.GetDirectoryName(project.ProjectFileName), projectRelativeFileName)
                let fileText = File.ReadAllText(filePath)
                BenchmarkAction.AnalyseFile {FileName = filePath; FileVersion = i; SourceText = fileText; Options = project; Repeat = repeat}
            )
        
        let config : BenchmarkConfig =
            {
                BenchmarkConfig.ProjectCacheSize = 200
            }
            
        {
            BenchmarkInputs.Actions = actions
            BenchmarkInputs.Config = config
        }
    
    let private makeInputsPath (codeRoot : string) =
        let artifactsDir = Path.Combine(codeRoot, ".artifacts")
        let dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss")
        Path.Combine(artifactsDir, $"{dateStr}.fcsinputs.json") 
    
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
        projInfoEnvVariables
        |> List.map (fun var -> var, "")
        
    module MSBuildProps =
        let makeDefault () =
            [
                "FcsReferenceType", "nuget"
                "FcsNugetVersion", "41.0.5"
            ]
        let makeDll (fcsDllPath : string) =
            [
                "FcsReferenceType", "dll"
                "FcsDllPath", fcsDllPath 
            ]
    
    let private resultsJsonPath (bdnArtifactDir : string) (benchmarkName : string) =
        Path.Combine(bdnArtifactDir, $@"results/{benchmarkName}-report.json")
    
    type RunResultSummary =
        {
            MeanS : double
            AllocatedMB : double
        }
    
    let extractResultsFromJson (summary : JObject) : RunResultSummary =
        let benchmark = summary["Benchmarks"][0]
        let stats = benchmark["Statistics"]
        let meanMicros = stats["Mean"].ToString() |> Double.Parse
        
        let metrics = benchmark["Metrics"] :?> JArray
        let allocatedBytes =
            let found =
                metrics
                |> Seq.find (fun m -> (m["Descriptor"]["Id"]).ToString() = "Allocated Memory")
            found["Value"].ToString() |> Double.Parse
            
        { MeanS = Math.Round(meanMicros / 1000000000.0, 3); AllocatedMB = Math.Round(allocatedBytes / 1024.0 / 1024.0, 3) }
        
    let private readBasicJsonResults (bdnArtifactsDir : string) (benchmarkClass : string) =
        let json = File.ReadAllText(resultsJsonPath bdnArtifactsDir benchmarkClass)
        let jObject = JsonConvert.DeserializeObject<JObject>(json)
        extractResultsFromJson jObject
    
    let private readJsonResultsSummary (bdnArtifactsDir : string) (benchmarkClass : string) =
        let jsonResultsPath = resultsJsonPath bdnArtifactsDir benchmarkClass
        let json = File.ReadAllText(jsonResultsPath)
        let jObject = JsonConvert.DeserializeObject<JObject>(json)
        extractResultsFromJson jObject
    
    let private makeVersionArg (version : NuGetFCSVersion) =
        match version with
        | NuGetFCSVersion.Official version -> $"--official={version}"
        | NuGetFCSVersion.Local sourceDir -> $"--local=\"{sourceDir}\""
    
    let private prepareAndRun (config : Config) (case : BenchmarkCase) (dryRun : bool) (cleanup : bool) (bdnArgs : string option) (versions : NuGetFCSVersion list) =
        let codebase = prepareCodebase config case
        let inputs = generateInputs case codebase.Path
        let inputsPath = makeInputsPath codebase.Path
        log.Information("Serializing inputs as {inputsPath}", inputsPath)        
        let serialized = serializeInputs inputs
        Directory.CreateDirectory(Path.GetDirectoryName(inputsPath)) |> ignore
        File.WriteAllText(inputsPath, serialized)
        
        if dryRun = false then
            use _ = LogContext.PushProperty("step", "Run")
            let workingDir = Path.Combine(Path.GetDirectoryName(Assembly.GetAssembly(typeof<RepoSetup.RepoSpec>).Location), "Benchmarks.Runner")
            let additionalEnvVariables =
                match config.FcsDllPath with
                | None -> MSBuildProps.makeDefault()
                | Some fcsDllPath ->
                    let absolutePath = Path.Combine(workingDir, fcsDllPath)
                    if File.Exists absolutePath |> not then
                        failwith $"Given FCS dll path doesn't exist: {absolutePath}"
                    MSBuildProps.makeDll fcsDllPath
            let envVariables =
                additionalEnvVariables
                @ emptyProjInfoEnvironmentVariables()
            let bdnArtifactsDir = Path.Combine(workingDir, "BenchmarkDotNet.Artifacts")
            let benchmarkClass = "Benchmarks.Runner.FCSBenchmark"
            let exe = "dotnet"
            let versionsArgs =
                let o =
                    versions
                    |> List.choose (function NuGetFCSVersion.Official v -> Some v | _ -> None)
                    |> function
                    | [] -> ""
                    | officials -> "--official " + String.Join(" ", officials)
                let l =
                    versions
                    |> List.choose (function NuGetFCSVersion.Local v -> Some v | _ -> None)
                    |> function
                    | [] -> ""
                    | locals -> "--local " + String.Join(" ", locals)
                o + " " + l
            let bdnArgs =
                seq {
                    match bdnArgs with Some bdnArgs -> yield bdnArgs | None -> ()
                    match config.Iterations with Some iterations -> yield $"--iterationCount={iterations}" | None -> ()
                }
                |> Seq.toList
                |> fun args ->
                    match args with
                    | [] -> ""
                    | args -> "--bdnargs \"" + String.Join(" ", args) + "\""
            let args = $"run -c Release -- --input={inputsPath} {versionsArgs} {bdnArgs}".Trim()
            log.Information("Starting the benchmark. Full BDN output can be found in {artifactFiles}. Full commandline: '{exe} {args}' in '{dir}'.", $"{bdnArtifactsDir}/*.log", exe, args, workingDir)
            Utils.runProcess exe args workingDir envVariables LogEventLevel.Verbose
            
            let res = readJsonResultsSummary bdnArtifactsDir benchmarkClass
            log.Information("Detailed results can be found in {resultsDir}.", Path.Combine(bdnArtifactsDir, "results"))
            log.Information("Result summary: Mean={mean:0.#}s, Allocated={allocatedMB:0}MB.", res.MeanS, res.AllocatedMB)
        else
            log.Information("Not running the benchmark as requested")
            
        match codebase, cleanup with
        | Local _, _ -> ()
        | Git _, false -> ()
        | Git repo, true ->
            log.Information("Cleaning up checked out git repo {repoPath} as requested", repo.Info.Path)
            Directory.Delete repo.Info.Path
    
    type Args =
        {
            [<CommandLine.Option('c', Default = ".artifacts", HelpText = "Base directory for git checkouts")>]
            CheckoutsDir : string
            [<CommandLine.Option('i', Required = true, HelpText = "Path to the input file describing the benchmark")>]
            Input : string
            [<CommandLine.Option("dry-run", HelpText = "If set, prepares the benchmark and prints the commandline to run it, then exits")>]
            DryRun : bool
            [<CommandLine.Option(Default = false, HelpText = "If set, removes the checkout directory afterwards. Doesn't apply to local codebases")>]
            Cleanup : bool
            [<CommandLine.Option('f', "fcsdll", HelpText = "Path to the FSharp.Compiler.Service.dll to benchmark - by default a NuGet package is used instead")>]
            FcsDllPath : string option
            [<CommandLine.Option('n', "iterations", HelpText = "Number of iterations to run")>]
            Iterations : int option
            [<CommandLine.Option('v', "verbose", Default = false, HelpText = "Verbose logging. Includes output of all preparation steps.")>]
            Verbose : bool
            [<CommandLine.Option('b', "bdnargs", HelpText = "Additional BDN arguments as a single string")>]
            BdnArgs : string option
            [<Option("official", Required = false, HelpText = "A list of publically available FCS NuGet versions to test.")>]
            OfficialVersions : string seq
            [<Option("local", Required = false, HelpText = "A list of local NuGet sources to use for testing locally-generated FCS nupkg files.")>]
            LocalNuGetSourceDirs : string seq
        }
    
    let run (args : Args) =
        
        log <-LoggerConfiguration().Enrich.FromLogContext()
                    .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {step:j}: {Message:lj}{NewLine}{Exception}")
                    .MinimumLevel.Is(if args.Verbose then LogEventLevel.Verbose else LogEventLevel.Information)
                    .CreateLogger()
        try
            log.Verbose("CLI args provided:" + Environment.NewLine + "{args}", args)
            
            let config =
                {
                    Config.CheckoutBaseDir = args.CheckoutsDir
                    Config.FcsDllPath = args.FcsDllPath
                    Config.Iterations = args.Iterations
                }
            let case =
                use _ = LogContext.PushProperty("step", "Read input")
                try
                    let path = args.Input
                    log.Verbose("Read and deserialize inputs from {path}", path)
                    path
                    |> File.ReadAllText
                    |> JsonConvert.DeserializeObject<BenchmarkCase>
                    |> fun case ->
                            let defaultCodebasePrep =
                                [
                                    {
                                        CodebasePrepStep.Command = "dotnet"
                                        CodebasePrepStep.Args = $"restore {case.SlnRelative}"
                                    }
                                ]
                            let codebasePrep =
                                match obj.ReferenceEquals(case.CodebasePrep, null) with
                                | true -> defaultCodebasePrep
                                | false -> case.CodebasePrep
                            
                            { case with CodebasePrep = codebasePrep }
                with e ->
                    let msg = $"Failed to read inputs file: {e.Message}"
                    log.Fatal(msg)
                    reraise()
            
            use _ = LogContext.PushProperty("step", "PrepareAndRun")
            let versions = parseVersions args.OfficialVersions args.LocalNuGetSourceDirs
            prepareAndRun config case args.DryRun args.Cleanup args.BdnArgs versions
        with ex ->
            if args.Verbose then
                log.Fatal(ex, "Failure.")
            else
                log.Fatal(ex, "Failure. Consider using --verbose for extra information.")
            
    [<EntryPoint>]
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let main args =
        let parseResult = Parser.Default.ParseArguments<Args> args
        parseResult
            .WithParsed(run)
        |> ignore        
        
        if parseResult.Tag = ParserResultType.Parsed then 0 else 1