module Buildalyzer.Go

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open Buildalyzer
open Buildalyzer.Environment
open FSharp.Compiler.CodeAnalysis

type CrackerOptions =
    {
        Configuration : string
        ProjFile : string
    }

module ReadOnlyDictionary =
    open System.Collections.Generic
    let tryFind key (dic: #IReadOnlyDictionary<'Key, 'Value>) =
        match dic.TryGetValue(key) with
        | true, v -> Some v
        | false, _ -> None

let normalizePath (path: string) =
    path.Replace('\\', '/').TrimEnd('/')

let normalizeFullPath (path: string) =
    let path = if System.String.IsNullOrWhiteSpace path then "." else path
    Path.GetFullPath(path).Replace('\\', '/')

let getRelativePath (path: string) (pathTo: string) =
    let path = if System.String.IsNullOrWhiteSpace path then "." else path
    Path.GetRelativePath(path, pathTo).Replace('\\', '/')


let private getDllName (dllFullPath: string) =
    let i = dllFullPath.LastIndexOf('/')
    dllFullPath.[(i + 1) .. (dllFullPath.Length - 5)] // -5 removes the .dll extension

let private isUsefulOption (opt : string) =
    [ "--define"
      "--nowarn"
      "--warnon"
    //   "--warnaserror" // Disable for now to prevent unexpected errors, see #2288
    //   "--langversion" // See getBasicCompilerArgs
    ]
    |> List.exists opt.StartsWith

type CrackedFsproj =
    { ProjectFile: string
      SourceFiles: string list
      ProjectReferences: string list
      DllReferences: IDictionary<string, string>
      // PackageReferences: FablePackage list
      OtherCompilerOptions: string list
      OutputType: string option }

let makeProjectOptions (opts: CrackerOptions) otherOptions sources: FSharpProjectOptions =
    let otherOptions = [|
        yield! otherOptions
        yield "--optimize-"
    |]
    { ProjectId = None
      ProjectFileName = opts.ProjFile
      OtherOptions = otherOptions
      SourceFiles = Array.distinct sources
      ReferencedProjects = [| |]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

let getCrackedFsproj (opts: CrackerOptions) (projOpts: string[]) (projRefs: string[]) outputType =
    // Use case insensitive keys, as package names in .paket.resolved
    // may have a different case, see #1227
    let dllRefs = Dictionary(StringComparer.OrdinalIgnoreCase)

    let sourceFiles, otherOpts =
        (projOpts, ([], []))
        ||> Array.foldBack (fun line (src, otherOpts) ->
            if line.StartsWith("-r:") then
                let line = normalizePath (line.[3..])
                let dllName = getDllName line
                dllRefs.Add(dllName, line)
                src, otherOpts
            elif isUsefulOption line then
                src, line::otherOpts
            elif line.StartsWith("-") then
                src, otherOpts
            else
                (normalizeFullPath line)::src, otherOpts)

    { ProjectFile = opts.ProjFile
      SourceFiles = sourceFiles
      ProjectReferences = projRefs |> Array.toList
      DllReferences = dllRefs
      // PackageReferences = fablePkgs
      OtherCompilerOptions = otherOpts
      OutputType = outputType }


let getProjectOptionsFromProjectFile =
    let mutable manager = None

    let compileFilesToAbsolutePath projDir (f: string) =
        if f.EndsWith(".fs") || f.EndsWith(".fsi") then
            if Path.IsPathRooted f then f else Path.Combine(projDir, f)
        else
            f
    fun (opts: CrackerOptions) projFile ->
        let manager =
            match manager with
            | Some m -> m
            | None ->
                let log = new System.IO.StringWriter()
                let options = AnalyzerManagerOptions(LogWriter = log)
                let m = AnalyzerManager(options)
                m.SetGlobalProperty("Configuration", opts.Configuration)
                manager <- Some m
                m

        let analyzer = manager.GetProject(projFile)
        let be = analyzer.EnvironmentFactory.GetBuildEnvironment()
        let be2 = BuildEnvironment(true, false, [||], be.MsBuildExePath, be.DotnetExePath, be.Arguments)
        // If the project targets multiple frameworks, multiple results will be returned
        // For now we just take the first one
        let result =
            match analyzer.Build(be2) |> Seq.toList with
            | result::_ -> result
            // TODO: Get Buildalyzer errors from the log
            | [] -> failwith $"Cannot parse {projFile}"
        let projDir = IO.Path.GetDirectoryName(projFile)
        let projOpts =
            // result.CompilerArguments doesn't seem to work well in Linux
            System.Text.RegularExpressions.Regex.Split(result.Command, @"\r?\n")
            |> Array.skipWhile (fun line -> not(line.StartsWith("-")))
            |> Array.map (compileFilesToAbsolutePath projDir)
        projOpts, Seq.toArray result.ProjectReferences, result.Properties


/// Use Buildalyzer to invoke MSBuild and get F# compiler args from an .fsproj file.
/// As we'll merge this later with other projects we'll only take the sources and
/// the references, checking if some .dlls correspond to Fable libraries
let fullCrack (opts: CrackerOptions): CrackedFsproj =
    // if not opts.NoRestore then
    //     Process.runSync (IO.Path.GetDirectoryName projFile) "dotnet" [
    //         "restore"
    //         IO.Path.GetFileName projFile
    //         for constant in opts.FableOptions.Define do
    //             $"-p:{constant}=true"
    //     ] |> ignore

    let projOpts, projRefs, msbuildProps =
        getProjectOptionsFromProjectFile opts opts.ProjFile

    // let targetFramework =
    //     match Map.tryFind "TargetFramework" msbuildProps with
    //     | Some targetFramework -> targetFramework
    //     | None -> failwithf "Cannot find TargetFramework for project %s" projFile

    let outputType = ReadOnlyDictionary.tryFind "OutputType" msbuildProps

    getCrackedFsproj opts projOpts projRefs outputType

/// For project references of main project, ignore dll and package references
let easyCrack (opts: CrackerOptions) dllRefs (projFile: string): CrackedFsproj =
    let projOpts, projRefs, msbuildProps =
        getProjectOptionsFromProjectFile opts projFile

    let outputType = ReadOnlyDictionary.tryFind "OutputType" msbuildProps
    let sourceFiles, otherOpts =
        (projOpts, ([], []))
        ||> Array.foldBack (fun line (src, otherOpts) ->
            if isUsefulOption line then
                src, line::otherOpts
            elif line.StartsWith("-") then
                src, otherOpts
            else
                (normalizeFullPath line)::src, otherOpts)

    { ProjectFile = projFile
      SourceFiles = sourceFiles
      ProjectReferences = projRefs |> Array.toList
      DllReferences = Dictionary()
      // PackageReferences = []
      OtherCompilerOptions = otherOpts
      OutputType = outputType}


let getCrackedProjectsFromMainFsproj (opts: CrackerOptions) =
    let mainProj = fullCrack opts
    let rec crackProjects (acc: CrackedFsproj list) (projFile: string) =
        let crackedFsproj =
            match acc |> List.tryFind (fun x -> x.ProjectFile = projFile) with
            | None -> easyCrack opts mainProj.DllReferences projFile
            | Some crackedFsproj -> crackedFsproj
        // Add always a reference to the front to preserve compilation order
        // Duplicated items will be removed later
        List.fold crackProjects (crackedFsproj::acc) crackedFsproj.ProjectReferences
    let refProjs =
        List.fold crackProjects [] mainProj.ProjectReferences
        |> List.distinctBy (fun x -> x.ProjectFile)
    refProjs, mainProj

let getCrackedProjects (opts: CrackerOptions) =
    match (Path.GetExtension opts.ProjFile).ToLower() with
    | ".fsproj" ->
        getCrackedProjectsFromMainFsproj opts
    | s -> failwithf $"Unsupported project type: %s{s}"

// It is common for editors with rich editing or 'intellisense' to also be watching the project
// file for changes. In some cases that editor will lock the file which can cause fable to
// get a read error. If that happens the lock is usually brief so we can reasonably wait
// for it to be released.
let retryGetCrackedProjects opts =
    let retryUntil = (DateTime.Now + TimeSpan.FromSeconds 2.)
    let rec retry () =
        try
            getCrackedProjects opts
        with
        | :? IO.IOException as ioex ->
            if retryUntil > DateTime.Now then
                System.Threading.Thread.Sleep 500
                retry()
            else
                failwithf $"IO Error trying read project options: %s{ioex.Message} "
        | _ -> reraise()
    retry()


let getBasicCompilerArgs () =
    [|
        // "--debug"
        // "--debug:portable"
        "--noframework"
        "--nologo"
        "--simpleresolution"
        "--nocopyfsharpcore"
        "--nowin32manifest"
        // "--nowarn:NU1603,NU1604,NU1605,NU1608"
        // "--warnaserror:76"
        "--warn:3"
        "--fullpaths"
        "--flaterrors"
        // Since net5.0 there's no difference between app/library
        // yield "--target:library"
    |]

let getFullProjectOpts (opts: CrackerOptions) =
    if not(IO.File.Exists(opts.ProjFile)) then
        failwith ("Project file does not exist: " + opts.ProjFile)

    let projRefs, mainProj = retryGetCrackedProjects opts

    let sourcePaths =
        mainProj.SourceFiles |> List.toArray

    let refOptions =
        projRefs
        |> List.collect (fun x -> x.OtherCompilerOptions)
        |> List.toArray

    let otherOptions =
        [|
            yield! refOptions // merged options from all referenced projects
            yield! mainProj.OtherCompilerOptions // main project compiler options
            yield! getBasicCompilerArgs() // options from compiler args
            yield "--optimize-"
        |]
        |> Array.distinct

    let dllRefs = mainProj.DllReferences.Values |> Seq.toArray

    let outputType = mainProj.OutputType
    let projRefs = projRefs |> List.map (fun p -> p.ProjectFile)
    let otherOptions = Array.append otherOptions dllRefs

    let precompiledInfo = None

    let fsharpOptions = makeProjectOptions opts otherOptions sourcePaths
    let refs = projRefs
    fsharpOptions, refs

let projPath = "c:/projekty/fsharp/fsharp_main/src/compiler/FSharp.Compiler.Service.fsproj"
let opts =
    {
        Configuration = "Debug"
        ProjFile = "c:/projekty/fsharp/fsharp_main/src/compiler/FSharp.Compiler.Service.fsproj"
    }
    
let sw = Stopwatch.StartNew()
let x = getProjectOptionsFromProjectFile opts projPath
printfn $"Build took {sw.Elapsed}"
let y = getFullProjectOpts opts
printfn $"Build took {sw.Elapsed}"

let amo = AnalyzerManagerOptions()
let sln = "c:/projekty/fsharp/fsharp_main/FSharp.sln"
let am = AnalyzerManager()
let p = am.GetProject("c:/projekty/fsharp/fsharp_main/src/compiler/FSharp.Compiler.Service.fsproj")
printfn $"%+A{p}"
0
