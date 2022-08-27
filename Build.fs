module FCSBenchmark.Generator.Build

open Serilog.Events

let buildAndPackFCS (repoRootDir : string) =
    log.Information($"Building and packing FCS in {repoRootDir}")
    Utils.runProcess "cmd" $"/C build.cmd -c Release -pack -noVisualStudio" repoRootDir [] LogEventLevel.Verbose