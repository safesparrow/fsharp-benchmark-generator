/// This file is shared between Benchmarks.Generator that serializes inputs using these DTO types,
/// and Benchmarks.Runner that deserializes them using these DTO types.
/// It's shared via link rather than a library.
/// There is no easy way for the two projects to reference a shared F# library because they have different FSharp.Core references
module Benchmarks.Common.Dtos

open System
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
        SourceFiles : System.Collections.Generic.List<string>
        OtherOptions: System.Collections.Generic.List<string>
        ReferencedProjects: System.Collections.Generic.List<FSharpReferenceDto>
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
        Actions : System.Collections.Generic.List<BenchmarkActionDto>
        Config : BenchmarkConfig
    }

/// Defines a call to BackgroundChecker.ParseAndCheckFileInProject(fileName: string, fileVersion, sourceText: ISourceText, options: FSharpProjectOptions, userOpName) =
type AnalyseFile =
    {
        FileName: string
        FileVersion: int
        SourceText: string
        Options: FSharpProjectOptions
    }

type BenchmarkAction =
    | AnalyseFile of AnalyseFile
    
type BenchmarkInputs =
    {
        Actions : BenchmarkAction list
        Config : BenchmarkConfig
    }

let rec private referenceToDto (rp : FSharpReferencedProject) : FSharpReferenceDto =
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

and private optionsToDto (o : FSharpProjectOptions) : FSharpProjectOptionsDto =
    {
        ProjectFileName = o.ProjectFileName
        ProjectId = o.ProjectId
        SourceFiles = o.SourceFiles |> System.Collections.Generic.List
        OtherOptions = o.OtherOptions |> System.Collections.Generic.List
        ReferencedProjects =
            o.ReferencedProjects
            |> Array.map referenceToDto
            |> System.Collections.Generic.List
        IsIncompleteTypeCheckEnvironment = o.IsIncompleteTypeCheckEnvironment
        UseScriptResolutionRules = o.UseScriptResolutionRules
        LoadTime = o.LoadTime
        Stamp = o.Stamp
    }
        
let actionToDto (action : BenchmarkAction) =
    match action with
    | BenchmarkAction.AnalyseFile x ->
        {
            AnalyseFileDto.FileName = x.FileName
            FileVersion = x.FileVersion
            SourceText = x.SourceText
            Options = x.Options |> optionsToDto
        }
        |> BenchmarkActionDto.AnalyseFile

let inputsToDtos (inputs : BenchmarkInputs) =
    {
        BenchmarkInputsDto.Actions = inputs.Actions |> List.map actionToDto |> System.Collections.Generic.List
        Config = inputs.Config
    }

let rec private optionsFromDto (o : FSharpProjectOptionsDto) : FSharpProjectOptions =       
    let fakeRP (rp : FSharpReferenceDto) : FSharpReferencedProject =
        let back = optionsFromDto rp.Options
        FSharpReferencedProject.CreateFSharp(rp.OutputFile, back)
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

let private actionFromDto (dto : BenchmarkActionDto) =
    match dto with
    | BenchmarkActionDto.AnalyseFile x ->
        {
            AnalyseFile.FileName = x.FileName
            FileVersion = x.FileVersion
            SourceText = x.SourceText
            Options = x.Options |> optionsFromDto
        }
        |> BenchmarkAction.AnalyseFile

let private inputsFromDto (dto : BenchmarkInputsDto) =
    {
        BenchmarkInputs.Actions = dto.Actions.ToArray() |> Seq.map actionFromDto |> Seq.toList 
        Config = dto.Config
    }

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
