/// This file is shared between FCSBenchmark.Generator that serializes inputs using these DTO types,
/// and FCSBenchmark.Runner that deserializes them using these DTO types.
/// It's shared via link rather than a library.
/// There is no easy way for the two projects to reference a shared F# library because they have different FSharp.Core references
module FCSBenchmark.Common.Dtos
#nowarn "40"

open System
open System.Collections.Generic
open System.ComponentModel
open System.Runtime.CompilerServices
open Microsoft.FSharp.Reflection
open FSharp.Compiler.CodeAnalysis
open Newtonsoft.Json

[<CLIMutable>]
type FSharpReferenceDto =
    {
        OutputFile: string
        Options : FSharpProjectOptionsDto
    }

and [<CLIMutable>] FSharpProjectOptionsDto =
    {
        ProjectFileName : string
        ProjectId : string option
        SourceFiles : List<string>
        OtherOptions: List<string>
        ReferencedProjects: List<FSharpReferenceDto>
        IsIncompleteTypeCheckEnvironment : bool
        UseScriptResolutionRules : bool
        LoadTime : DateTime
        Stamp: int64 option
    }

[<CLIMutable>]
type AnalyseFileDto =
    {
        FileName: string
        FileVersion: int
        SourceText: string
        Options: FSharpProjectOptionsDto
        [<DefaultValue(1)>]
        Repeat : int
    }

type BenchmarkActionDto =
    | AnalyseFile of AnalyseFileDto

[<CLIMutable>]    
type BenchmarkConfig =
    {
        ProjectCacheSize : int
    }
    with static member makeDefault () = {ProjectCacheSize = 200}
    
[<CLIMutable>]
type BenchmarkInputsDto =
    {
        Actions : List<BenchmarkActionDto>
        Config : BenchmarkConfig
    }

/// Defines a call to BackgroundChecker.ParseAndCheckFileInProject(fileName: string, fileVersion, sourceText: ISourceText, options: FSharpProjectOptions, userOpName) =
type AnalyseFile =
    {
        FileName: string
        FileVersion: int
        SourceText: string
        Options: FSharpProjectOptions
        Repeat: int
    }

type BenchmarkAction =
    | AnalyseFile of AnalyseFile
    
type BenchmarkInputs =
    {
        Actions : BenchmarkAction list
        Config : BenchmarkConfig
    }

let private memoize fn =
    let refComparer =
        {
            new IEqualityComparer<'a> with
                member this.Equals(a, b) = obj.ReferenceEquals(a, b)
                member this.GetHashCode(a) = RuntimeHelpers.GetHashCode(a)
        }
    let cache = Dictionary<_,_>(refComparer)
    fun x ->
        match cache.TryGetValue x with
        | true, v -> v
        | false, _ ->
            let v = fn x
            cache.Add(x,v)
            v

let rec private referenceToDto =
    fun (rp : FSharpReferencedProject) -> 
        // Reflection is needed since DU cases are internal.
        // The alternative is to add an [<InternalsVisibleTo>] entry to the FCS project
        let c, fields = FSharpValue.GetUnionFields(rp, typeof<FSharpReferencedProject>, true)
        match c.Name with
        | "FSharpReference" ->
            let outputFile = fields[0] :?> string
            let options = fields[1] :?> FSharpProjectOptions
            let fakeOptions = optionsToDto options
            {
                FSharpReferenceDto.OutputFile = outputFile
                FSharpReferenceDto.Options = fakeOptions
            }
        | _ -> failwith $"Unsupported {nameof(FSharpReferencedProject)} DU case: {c.Name}. only 'FSharpReference' is supported by the serializer"
    |> memoize

and private optionsToDto =
    let mutable stamp = 1L
    let nextStamp () =
        stamp <- stamp + 1L
        stamp
    fun (o : FSharpProjectOptions) ->
        {
            ProjectFileName = o.ProjectFileName
            ProjectId = o.ProjectId
            SourceFiles = o.SourceFiles |> List
            OtherOptions = o.OtherOptions |> List
            ReferencedProjects =
                o.ReferencedProjects
                |> Array.map referenceToDto
                |> List
            IsIncompleteTypeCheckEnvironment = o.IsIncompleteTypeCheckEnvironment
            UseScriptResolutionRules = o.UseScriptResolutionRules
            LoadTime = o.LoadTime
            // We always override the Stamp provided.
            // This is to avoid FCS spending a huge amount of time comparing projects when all Stamps are None
            Stamp = nextStamp() |> Some 
        }
    |> memoize
        
let actionToDto =
    fun (action : BenchmarkAction) ->
        match action with
        | BenchmarkAction.AnalyseFile x ->
            {
                AnalyseFileDto.FileName = x.FileName
                FileVersion = x.FileVersion
                SourceText = x.SourceText
                Options = x.Options |> optionsToDto
                Repeat = x.Repeat
            }
            |> BenchmarkActionDto.AnalyseFile
    |> memoize

let inputsToDtos =
    fun (inputs : BenchmarkInputs) ->
        {
            BenchmarkInputsDto.Actions = inputs.Actions |> List.map actionToDto |> List
            Config = inputs.Config
        }
    |> memoize

let rec private fakeRP =
    fun (rp : FSharpReferenceDto) ->
        let back = optionsFromDto rp.Options
        FSharpReferencedProject.CreateFSharp(rp.OutputFile, back)
    |> memoize

and private optionsFromDto =
    fun (o : FSharpProjectOptionsDto) ->
        {
            ProjectFileName = o.ProjectFileName
            ProjectId = o.ProjectId
            SourceFiles = o.SourceFiles.ToArray()
            OtherOptions = o.OtherOptions.ToArray()
            ReferencedProjects =
                o.ReferencedProjects.ToArray()
                |> Array.map fakeRP            
            IsIncompleteTypeCheckEnvironment = o.IsIncompleteTypeCheckEnvironment
            UseScriptResolutionRules = o.UseScriptResolutionRules
            LoadTime = o.LoadTime
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = o.Stamp
        }
    |> memoize

let private actionFromDto =
    fun (dto : BenchmarkActionDto) ->
        match dto with
        | BenchmarkActionDto.AnalyseFile x ->
            {
                AnalyseFile.FileName = x.FileName
                FileVersion = x.FileVersion
                SourceText = x.SourceText
                Options = x.Options |> optionsFromDto
                Repeat = x.Repeat
            }
            |> BenchmarkAction.AnalyseFile
    |> memoize

let private inputsFromDto =
    fun (dto : BenchmarkInputsDto) ->
        {
            BenchmarkInputs.Actions = dto.Actions.ToArray() |> Seq.map actionFromDto |> Seq.toList 
            Config = dto.Config
        }
    |> memoize

let jsonSerializerSettings =
    JsonSerializerSettings(
        PreserveReferencesHandling = PreserveReferencesHandling.All
    )

let serializeInputs (inputs : BenchmarkInputs) : string =
    let dto = inputs |> inputsToDtos
    JsonConvert.SerializeObject(dto, Formatting.Indented, jsonSerializerSettings)
        
let deserializeInputs (json : string) : BenchmarkInputs =
    let dto = JsonConvert.DeserializeObject<BenchmarkInputsDto>(json, jsonSerializerSettings)
    inputsFromDto dto
    
[<RequireQualifiedAccess>]
type NuGetFCSVersion =
    | Official of version : string
    | Local of sourceDir : string
    
let parseVersions (officialVersions : string seq) (localNuGetSourceDirs : string seq) =
    let official = officialVersions |> Seq.map NuGetFCSVersion.Official
    let local = localNuGetSourceDirs |> Seq.map NuGetFCSVersion.Local
    Seq.append official local
    |> Seq.toList
    |> function
        | [] -> failwith "At least one version must be specified"
        | versions -> versions
