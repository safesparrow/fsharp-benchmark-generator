module FCSBenchmark.Generator.Generate

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
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
            let repo = prepareRepo config case.Repo
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

let rec copyRunnerProjectFilesToTemp (sourceDir : string) =
    let buildDir =
        let file = Path.GetTempFileName()
        File.Delete(file)
        Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file))
    try
        Directory.CreateDirectory(buildDir) |> ignore
        for dirName in ["FCSBenchmark.Runner"; "FCSBenchmark.Serialisation"] do
            let sourceDir = Path.Combine(sourceDir, dirName)
            let targetDir = Path.Combine(buildDir, dirName)
            Directory.CreateDirectory(targetDir) |> ignore
            Directory.EnumerateFiles(sourceDir)
            |> Seq.iter (fun sourceFile ->
                File.Copy(sourceFile, Path.Combine(targetDir, Path.GetFileName(sourceFile)))
            )
        Path.Combine(buildDir, "FCSBenchmark.Runner")
    with _ ->
        Directory.Delete(buildDir, recursive = true)
        reraise()
    

let private prepareAndRun (config : Config) (case : BenchmarkCase) (dryRun : bool) (cleanup : bool) (iterations : int) (warmups : int) (versions : NuGetFCSVersion list) =
    let codebase = prepareCodebase config case
    let inputs = generateInputs case codebase.Path
    let inputsPath = makeInputsPath codebase.Path
    log.Information("Serializing inputs as {inputsPath}", inputsPath)        
    let serialized = serializeInputs inputs
    Directory.CreateDirectory(Path.GetDirectoryName(inputsPath)) |> ignore
    File.WriteAllText(inputsPath, serialized)
    
    if dryRun = false then
        use _ = LogContext.PushProperty("step", "Run")
        let workingDir = Path.GetDirectoryName(Assembly.GetAssembly(typeof<RepoSpec>).Location)
        let workingDir = copyRunnerProjectFilesToTemp workingDir
        let envVariables = emptyProjInfoEnvironmentVariables() @ ["DOTNET_ROOT_X64", ""]
        let bdnArtifactsDir = Path.Combine(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts")
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
                | locals ->
                    "--local " + String.Join(" ", locals |> List.map (fun local -> local.TrimEnd([|'\\'; '/'|])))
            o + " " + l
        let artifactsPath = Path.Combine(Environment.CurrentDirectory, "FCSBenchmark.Artifacts")
        let args = $"run -c Release -- --artifacts-path={artifactsPath} --input={inputsPath} --iterations={iterations} --warmups={warmups} {versionsArgs}".Trim()
        log.Information(
            "Starting the benchmark:\n\
             - Full BDN output can be found in {artifactFiles}.\n\
             - Full commandline: '{exe} {args}'\n\
             - Working directory: '{dir}'.", $"{bdnArtifactsDir}/*.log", exe, args, workingDir)
        Utils.runProcess exe args workingDir envVariables LogEventLevel.Information
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
        [<CommandLine.Option('c', "checkouts", Default = ".artifacts", HelpText = "Base directory for git checkouts")>]
        CheckoutsDir : string
        [<CommandLine.Option("forceFcsBuild", Default = false, HelpText = "Force build git-sourced FCS versions even if the binaries already exist")>]
        ForceFCSBuild : bool
        [<CommandLine.Option('i', SetName = "input", HelpText = "Path to the input file describing the benchmark")>]
        Input : string
        [<CommandLine.Option("sample", SetName = "input", HelpText = "Use a predefined sample benchmark with the given name")>]
        SampleInput : string
        [<CommandLine.Option("dry-run", HelpText = "If set, prepares the benchmark and prints the commandline to run it, then exits")>]
        DryRun : bool
        [<CommandLine.Option(Default = false, HelpText = "If set, removes the checkout directory afterwards. Doesn't apply to local codebases")>]
        Cleanup : bool
        [<CommandLine.Option('n', "iterations", Default = 1, HelpText = "Number of iterations to run")>]
        Iterations : int
        [<CommandLine.Option('w', "warmups", Default = 1, HelpText = "Number of warmups to run")>]
        Warmups : int
        [<CommandLine.Option('v', "verbose", Default = false, HelpText = "Verbose logging. Includes output of all preparation steps.")>]
        Verbose : bool
        [<Option("official", Required = false, HelpText = "A publicly available FCS NuGet version to test. Supports multiple values.")>]
        OfficialVersions : string seq
        [<Option("local", Required = false, HelpText = "A local NuGet source to use for testing locally-generated FCS nupkg files. Supports multiple values.")>]
        LocalNuGetSourceDirs : string seq
        [<Option("github", Required = false, HelpText = "An FSharp repository&revision, in the form 'owner/repo/revision' eg. 'dotnet/fsharp/5a72e586278150b7aea4881829cd37be872b2043. Supports multiple values.")>]
        GitHubVersions : string seq
    }

let prepareCase (args : Args) : BenchmarkCase =
    use _ = LogContext.PushProperty("step", "Read input")
    try
        let path =
            match args.Input |> Option.ofObj, args.SampleInput |> Option.ofObj with
            | None, None -> failwith $"No input specified"
            | Some input, _ -> input
            | None, Some sample ->
                let assemblyDir = Path.GetDirectoryName(Assembly.GetAssembly(typeof<Args>).Location)
                let path = Path.Combine(assemblyDir, "inputs", $"{sample}.json")
                if File.Exists(path) then path
                else
                    let dir = Path.GetDirectoryName(path)
                    if Directory.Exists(dir) then
                        let samples =
                            Directory.EnumerateFiles(dir, "*.json")
                            |> Seq.map Path.GetFileNameWithoutExtension
                        let samplesString =
                            let str = String.Join(", ", samples)
                            $"[{str}]"
                        failwith $"Sample {path} does not exist. Available samples are: {samplesString}"
                    else
                        failwith $"Samples directory '{dir}' does not exist"
                    
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
            let m = Regex.Match(specStr, "^([0-9a-zA-Z_\-]+)/([0-9a-zA-Z_\-]+)/([0-9a-zA-Z_\-]+)$")
            if m.Success then
                let owner, repo, revision = m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value
                let url = $"https://github.com/{owner}/{repo}"
                let spec = FCSRepoSpec.Custom {GitUrl = url; Revision = revision}
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
    log <- LoggerConfiguration().Enrich.FromLogContext()
               .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {step:j}: {Message:lj}{NewLine}{Exception}")
               .MinimumLevel.Is(if args.Verbose then LogEventLevel.Verbose else LogEventLevel.Information)
               .CreateLogger()
    try
        log.Verbose("CLI args provided:" + Environment.NewLine + "{args}", args)
        let config =
            {
                Config.BaseDir = args.CheckoutsDir
                Config.ForceFCSBuild = args.ForceFCSBuild
            }
        
        let rawVersions = { Official = args.OfficialVersions |> Seq.toList; Local = args.LocalNuGetSourceDirs |> Seq.toList; Git = args.GitHubVersions |> Seq.toList }
        let versions = prepareFCSVersions config rawVersions
        let case = prepareCase args
        
        use _ = LogContext.PushProperty("step", "PrepareAndRun")
        prepareAndRun config case args.DryRun args.Cleanup args.Iterations args.Warmups versions
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