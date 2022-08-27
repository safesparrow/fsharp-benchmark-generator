[<AutoOpen>]
module FCSBenchmark.Generator.Log

open Serilog

let mutable internal log : ILogger = null
