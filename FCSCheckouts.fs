/// Preparing FCS codebase
module FCSBenchmark.Generator.FCSCheckouts

open System.IO
open FCSBenchmark.Generator.RepoSetup
open LibGit2Sharp
open Serilog.Events

type FCSGeneralRepoSpec =
    {
        GitUrl : string
        Revision : string
    }
    
type FCSMainRepoSpec =
    {
        Revision : string
    }

type FCSRepoSpec =
    | Main of FCSMainRepoSpec
    | Custom of FCSGeneralRepoSpec

module FCSRepoSpec =
    let toFullSpec (options : FCSRepoSpec) =
        match options with
        | FCSRepoSpec.Main {Revision = revision} ->
            {
                GitUrl = ""
                Revision = revision
            }
        | FCSRepoSpec.Custom spec -> spec
        |> function
            | spec ->
                {
                    Name = "__fsharp"
                    GitUrl = spec.GitUrl
                    Revision = spec.Revision
                }

let private fcsDllPath (checkoutDir : string) =
    Path.Combine(checkoutDir, "artifacts/bin/FSharp.Compiler.Service/Release/netstandard2.0/FSharp.Compiler.Service.dll")
    
let checkoutContainsBuiltFcs (checkoutDir : string) =
    File.Exists(fcsDllPath checkoutDir)

let buildAndPackFCS (repoRootDir : string) : unit =
    log.Information($"Building and packing FCS in {repoRootDir}")
    Utils.runProcess "cmd" $"/C build.cmd -c Release -pack -noVisualStudio" repoRootDir [] LogEventLevel.Verbose

type FCSCheckout =
    {
        Spec : FCSRepoSpec
        Repo : Repository
    }
    with
        member this.FullSpec = this.Spec |> FCSRepoSpec.toFullSpec
        member this.Dir = this.Repo.Info.WorkingDirectory
        member this.PackDir = this.Dir // Root dir can be provided to the runner instead of the packages directory
        
let checkoutAndBuild (config : Config) (fcsSpec : FCSRepoSpec) : FCSCheckout =
    let spec = FCSRepoSpec.toFullSpec fcsSpec
    let repo = prepare config spec
    buildAndPackFCS repo.Info.WorkingDirectory
    {
        Spec = fcsSpec
        Repo = repo
    }