module FCSBenchmark.Serialisation.Options

#nowarn "40"

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Microsoft.FSharp.Reflection
open FSharp.Compiler.CodeAnalysis

let internal memoizeUsingRefComparison fn =
    let refComparer =
        { new IEqualityComparer<'a> with
            member this.Equals (a, b) = obj.ReferenceEquals (a, b)
            member this.GetHashCode (a) = RuntimeHelpers.GetHashCode (a)
        }

    let cache = Dictionary<_, _> (refComparer)

    fun x ->
        match cache.TryGetValue x with
        | true, v -> v
        | false, _ ->
            let v = fn x
            cache.Add (x, v)
            v

[<CLIMutable>]
type FSharpReferenceDto =
    {
        OutputFile : string
        Options : FSharpProjectOptionsDto
    }

and [<CLIMutable>] FSharpProjectOptionsDto =
    {
        ProjectFileName : string
        ProjectId : string option
        SourceFiles : List<string>
        OtherOptions : List<string>
        ReferencedProjects : List<FSharpReferenceDto>
        IsIncompleteTypeCheckEnvironment : bool
        UseScriptResolutionRules : bool
        LoadTime : DateTime
        Stamp : int64 option
    }

let rec internal referenceToDto =
    fun (rp : FSharpReferencedProject) ->
        // Reflection is needed since DU cases are internal.
        // The alternative is to add an [<InternalsVisibleTo>] entry to the FCS project
        let c, fields =
            FSharpValue.GetUnionFields (rp, typeof<FSharpReferencedProject>, true)

        match c.Name with
        | "FSharpReference" ->
            let outputFile = fields[0] :?> string
            let options = fields[1] :?> FSharpProjectOptions
            let fakeOptions = optionsToDto options

            {
                FSharpReferenceDto.OutputFile = outputFile
                FSharpReferenceDto.Options = fakeOptions
            }
        | _ ->
            failwith
                $"Unsupported {nameof (FSharpReferencedProject)} DU case: {c.Name}. only 'FSharpReference' is supported by the serializer"
    |> memoizeUsingRefComparison

and internal optionsToDto =
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
            ReferencedProjects = o.ReferencedProjects |> Array.map referenceToDto |> List
            IsIncompleteTypeCheckEnvironment = o.IsIncompleteTypeCheckEnvironment
            UseScriptResolutionRules = o.UseScriptResolutionRules
            LoadTime = o.LoadTime
            // We always override the Stamp provided.
            // This is to avoid FCS spending a huge amount of time comparing projects when all Stamps are None
            Stamp = nextStamp () |> Some
        }
    |> memoizeUsingRefComparison

let rec private fakeRP =
    fun (rp : FSharpReferenceDto) ->
        let back = optionsFromDto rp.Options
        FSharpReferencedProject.CreateFSharp (rp.OutputFile, back)
    |> memoizeUsingRefComparison

and internal optionsFromDto =
    fun (o : FSharpProjectOptionsDto) ->
        {
            ProjectFileName = o.ProjectFileName
            ProjectId = o.ProjectId
            SourceFiles = o.SourceFiles.ToArray ()
            OtherOptions = o.OtherOptions.ToArray ()
            ReferencedProjects = o.ReferencedProjects.ToArray () |> Array.map fakeRP
            IsIncompleteTypeCheckEnvironment = o.IsIncompleteTypeCheckEnvironment
            UseScriptResolutionRules = o.UseScriptResolutionRules
            LoadTime = o.LoadTime
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = o.Stamp
        }
    |> memoizeUsingRefComparison
