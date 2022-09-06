/// Preparing FCS codebase
module FCSBenchmark.Generator.FCSCheckouts

open System.IO
open FCSBenchmark.Common.Dtos
open FCSBenchmark.Generator.RepoSetup
open LibGit2Sharp
open Serilog.Events

type FCSGeneralRepoSpec = { GitUrl : string ; Revision : string }

type FCSMainRepoSpec = { Revision : string }

type FCSRepoSpec =
    | Main of FCSMainRepoSpec
    | Custom of FCSGeneralRepoSpec

module FCSRepoSpec =
    let toFullSpec (options : FCSRepoSpec) =
        match options with
        | FCSRepoSpec.Main { Revision = revision } -> { GitUrl = "" ; Revision = revision }
        | FCSRepoSpec.Custom spec -> spec
        |> function
            | spec ->
                {
                    Name = "__fsharp"
                    GitUrl = spec.GitUrl
                    Revision = spec.Revision
                }

let private fcsDllPath (checkoutDir : string) =
    Path.Combine (
        checkoutDir,
        "artifacts/bin/FSharp.Compiler.Service/Release/netstandard2.0/FSharp.Compiler.Service.dll"
    )

let checkoutContainsBuiltFcs (checkoutDir : string) = File.Exists (fcsDllPath checkoutDir)

let buildAndPackFCS (repoRootDir : string) (forceFCSBuild : bool) : unit =
    let packagesDir =
        Path.Combine (repoRootDir, "artifacts", "packages", "Release", "Release")

    let packagesExist =
        if Directory.Exists (packagesDir) then
            match
                Directory.EnumerateFiles (packagesDir, "FSharp.Compiler.Service.*.nupkg")
                |> Seq.toList
            with
            | [] -> false
            | _ -> true
        else
            log.Information ($"PackagesDir {packagesDir} DOES NOT exist")
            false

    let build () =
        log.Information ("Building and packing FCS in {repo}.", repoRootDir)
        Utils.runProcess "cmd" $"/C build.cmd -c Release -pack -noVisualStudio" repoRootDir [] LogEventLevel.Verbose

    match packagesExist, forceFCSBuild with
    | false, _ ->
        log.Information ($"packages don't exist - building")
        build ()
    | true, false -> log.Information ("FCS nupkg file exists in {repo} codebase - not building again.", repoRootDir)
    | true, true ->
        log.Information ("FCS nupkg file exists in {repo} codebase, but forceFCSBuild was set.", repoRootDir)
        build ()

type FCSCheckout =
    {
        Spec : FCSRepoSpec
        Repo : Repository
    }

    member this.FullSpec = this.Spec |> FCSRepoSpec.toFullSpec
    member this.Dir = this.Repo.Info.WorkingDirectory
    member this.PackDir = this.Dir // Root dir can be provided to the runner instead of the packages directory

let checkoutAndBuild (config : Config) (fcsSpec : FCSRepoSpec) : FCSCheckout =
    let spec = FCSRepoSpec.toFullSpec fcsSpec
    let repo = prepareRepo config spec
    buildAndPackFCS repo.Info.WorkingDirectory config.ForceFCSBuild
    { Spec = fcsSpec ; Repo = repo }

type NuGetVersion = | NuGetVersion of string

[<RequireQualifiedAccess>]
type FCSVersion =
    | OfficialNuGet of NuGetVersion
    | Local of DirectoryInfo // Root directory or package directory
    | Git of FCSRepoSpec

[<RequireQualifiedAccess>]
type NuGetFCSVersion =
    | Official of version : string
    | Local of sourceDir : string

let prepareFCSVersions (config : Config) (versions : FCSVersion list) : NuGetFCSVersion list =
    versions
    |> List.map (
        function
        | FCSVersion.OfficialNuGet (NuGetVersion version) -> NuGetFCSVersion.Official version
        | FCSVersion.Local root -> NuGetFCSVersion.Local root.FullName
        | FCSVersion.Git spec ->
            let checkout = checkoutAndBuild config spec
            NuGetFCSVersion.Local checkout.Dir

    )
