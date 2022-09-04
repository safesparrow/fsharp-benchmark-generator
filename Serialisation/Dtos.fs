/// This file is shared between FCSBenchmark.Generator that serializes inputs using these DTO types,
/// and FCSBenchmark.Runner that deserializes them using these DTO types.
module FCSBenchmark.Common.Dtos
#nowarn "40"

open System.Collections.Generic
open System.ComponentModel
open FCSBenchmark.Serialisation.Options
open FSharp.Compiler.CodeAnalysis
open Newtonsoft.Json

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
    |> memoizeUsingRefComparison

let inputsToDtos =
    fun (inputs : BenchmarkInputs) ->
        {
            BenchmarkInputsDto.Actions = inputs.Actions |> List.map actionToDto |> List
            Config = inputs.Config
        }
    |> memoizeUsingRefComparison

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
    |> memoizeUsingRefComparison

let private inputsFromDto =
    fun (dto : BenchmarkInputsDto) ->
        {
            BenchmarkInputs.Actions = dto.Actions.ToArray() |> Seq.map actionFromDto |> Seq.toList 
            Config = dto.Config
        }
    |> memoizeUsingRefComparison

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
    
type ParallelAnalysisMode =
    | Off = 0
    | On = 1
    | Compare = 2
    
type GCMode =
    | Workstation = 0
    | Server = 1
    | Compare = 2