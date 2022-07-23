﻿module Benchmarks.Generator.Generate

open System
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open Benchmarks.Common.Dtos
open CommandLine
open CommandLine.Text
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open Ionide.ProjInfo.Types
open Newtonsoft.Json
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
        p.ErrorDataReceived.Add(fun args -> log.Error(args.Data))
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
            with override this.ToString() = $"{this.Name} - {this.GitUrl} at revision {this.Revision}"
        
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
    
    type CheckAction =
        {
            FileName : string
            ProjectName : string
        }

    type CodebaseSourceType = Local | Git

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
            Iterations : int
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
    
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private doLoadOptions (toolsPath : ToolsPath) (sln : string) =
        // TODO allow customization of build properties
        let props = []
        let loader = WorkspaceLoader.Create(toolsPath, props)
        let vs = Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()        
        let projects = loader.LoadSln(sln, [], BinaryLogGeneration.Off) |> Seq.toList
        log.Information("{projectsCount} projects loaded from {sln}", projects.Length, sln)
        if projects.Length = 0 then
            failwith $"No projects were loaded from {sln} - this indicates an error in cracking the projects"
        
        let fsOptions =
            projects
            |> List.map (fun project -> Path.GetFileNameWithoutExtension(project.ProjectFileName), FCS.mapToFSharpProjectOptions project projects)
        fsOptions
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
            |> List.mapi (fun i {FileName = projectRelativeFileName; ProjectName = projectName} ->
                let project = options[projectName]
                let filePath = Path.Combine(Path.GetDirectoryName(project.ProjectFileName), projectRelativeFileName)
                let fileText = File.ReadAllText(filePath)
                BenchmarkAction.AnalyseFile {FileName = filePath; FileVersion = i; SourceText = fileText; Options = project}
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
            ]
        let makeDll (fcsDllPath : string) =
            [
                "FcsReferenceType", "dll"
                "FcsDllPath", fcsDllPath 
            ]
    
    let private prepareAndRun (config : Config) (case : BenchmarkCase) (doRun : bool) (cleanup : bool) =
        let codebase = prepareCodebase config case
        let inputs = generateInputs case codebase.Path
        let inputsPath = makeInputsPath codebase.Path
        log.Information("Serializing inputs as {inputsPath}", inputsPath)        
        let serialized = serializeInputs inputs
        Directory.CreateDirectory(Path.GetDirectoryName(inputsPath)) |> ignore
        File.WriteAllText(inputsPath, serialized)
        
        if doRun then
            use _ = LogContext.PushProperty("step", "Run")
            let workingDir = "Benchmarks.Runner"
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
            log.Information("Starting the benchmark")
            Utils.runProcess "dotnet" $"run -c Release -- {inputsPath} {config.Iterations}" workingDir envVariables LogEventLevel.Information
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
            [<CommandLine.Option(Default = true, HelpText = "If set to false, prepares the benchmark and prints the commandline to run it, then exits")>]
            Run : bool
            [<CommandLine.Option(Default = false, HelpText = "If set, removes the checkout directory afterwards. Doesn't apply to local codebases")>]
            Cleanup : bool
            [<CommandLine.Option('f', HelpText = "Path to the FSharp.Compiler.Service.dll to benchmark - by default a NuGet package is used instead")>]
            FcsDllPath : string option
            [<CommandLine.Option('n', Default = 1, HelpText = "Number of iterations to run")>]
            Iterations : int
            [<CommandLine.Option('v', Default = false, HelpText = "Verbose logging. Includes output of ")>]
            Verbose : bool
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
            prepareAndRun config case args.Run args.Cleanup
        with ex ->
            if args.Verbose then
                log.Fatal(ex, "Failure.")
            else
                log.Fatal(ex, "Failure. Consider using --verbose for extra information.")
            
    
    let help result (errors : Error seq) =
        let helpText =
            let f (h:HelpText) =
                h.AdditionalNewLineAfterOption <- false
                h.Heading <- "FCS Benchmark Generator"
                h
            HelpText.AutoBuild(result, f, id)
        printfn $"{helpText}"
    
    [<EntryPoint>]
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let main args =
        let parseResult = Parser.Default.ParseArguments<Args> args
        parseResult
            .WithParsed(run)
            .WithNotParsed(fun errors -> help parseResult errors)
        |> ignore        
        
        if parseResult.Tag = ParserResultType.Parsed then 0 else 1