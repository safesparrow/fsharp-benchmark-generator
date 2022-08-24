# Benchmarks.Generator

## What is it
A command-line app for generating and running benchmarks of FCS using high-level analysis definitions

Its goal is to make it easy to run FCS benchmarks that are:
* high-level
* using real, publicly-available codebase examples
* reproducible

## How it works
### Dependency graph
Below chart describes dependencies of the two main components - generator and runner.

Note that the generator is completely independent from the runner, including having different FCS references. 
```mermaid
%%{init: {'theme':'base'}}%%
graph LR;
    subgraph Generation
        A1(Ionide.ProjInfo.FCS) --> A2(Benchmarks.Generator)
        A3 --> A1
        A3(FSharp.Compiler.Service NuGet) --> A2
        style A3 fill:#ddf
    end

    A2 -.->|JSON| R2
    
    subgraph Running
        R1(FSharp.Compiler.Service source or NuGet) --> R2(Benchmarks.Runner)
        style R1 fill:#dfd
    end
```
### Process steps graph
Below chart describes the steps performed during a benchmark run and their dependencies, including any preparation steps
```mermaid
%%{init: {'theme':'base'}}%%
graph TD;
    AA(description.json)
    AA-->A
    AB(GitHub/Git server)
    AB-->|libgit2sharp|B
    subgraph Benchmarks.Generator process
        A(Codebase spec and analysis actions)-->B(Locally checked out codebase);
        B-->|Codebase prep steps| B1(Prepared codebase)
        B1-->|Ionide.ProjInfo.FCS| C(FSharpProjectOptions)
        A-->D(BenchmarkSpec)
        C-->D
        D-->E(JSON-friendly DTO)
    end
    E-->|Newtonsoft.Json|F(FCS inputs.json)
    F-->|Newtonsoft.Json|G(JSON-friendly DTO)
    B1-->K
    subgraph Benchmarks.Runner process
        G-->H(BenchmarkSpec')
        J(FSharp.Compiler.Service source)-->K(Benchmarks.Runner)
        H-->K
    end
```
## How to use it
Below command runs `Benchmark.Generator` and tests the analysis of the [Fantomas](https://github.com/fsprojects/fantomas) project using 3 different FCS versions: 
```bash
dotnet run -c Release --project ./Benchmarks.Generator.fsproj -- -i .\inputs\fantomas.json --official 41.0.5 41.0.2 --local c:\projekty\fsharp\fsharp_main -n 1 
```
Let's deconstruct it:
- `dotnet run -c Release --project ./Benchmarks.Generator.fsproj` - boilerplate required to start the program
- ` -- ` - indicates that the following arguments should be forwarded to the app rather than consumed by `dotnet`
- `-i .\inputs\fantomas.json` - points to a benchmark definition
- `--official 41.0.5 41.0.2` - specifies two FCS versions to be, available on public NuGet feed
- `--local c:\projekty\fsharp\fsharp_main` - specifies one local version of FCS to use, by pointing to a repository root - this assumes FCS has been packed using `./build.cmd -pack ...` command
For more CLI options use `dotnet run --help`

<details>
<summary>Sample output:</summary>

```bash
[23:34:17 INF] PrepareCodebase: Preparing repo fantomas (https://github.com/fsprojects/fantomas) @ 0fe6785076e045f28e4c88e6a57dd09b649ce671
[23:34:17 INF] PrepareCodebase: .artifacts\fantomas\0fe6785076e045f28e4c88e6a57dd09b649ce671 already exists - will assume the correct repository is already checked out
[23:34:17 INF] PrepareCodebase: Running 3 codebase prep steps
[23:34:20 INF] LoadOptions: 7 projects loaded from C:\projekty\fsharp\fsharp-benchmark-generator\.artifacts\fantomas\0fe6785076e045f28e4c88e6a57dd09b649ce671\fantomas.sln
[23:34:20 INF] PrepareAndRun: Serializing inputs as C:\projekty\fsharp\fsharp-benchmark-generator\.artifacts\fantomas\0fe6785076e045f28e4c88e6a57dd09b649ce671\.artifacts\2022-08-24_22-34-20.fcsinputs.json
[23:34:20 INF] Run: Starting the benchmark:
- Full BDN output can be found in C:\projekty\fsharp\fsharp-benchmark-generator\bin\Release\net6.0\Benchmarks.Runner\BenchmarkDotNet.Artifacts/*.log.
- Full commandline: 'dotnet run -c Release -- --input=C:\projekty\fsharp\fsharp-benchmark-generator\.artifacts\fantomas\0fe6785076e045f28e4c88e6a57dd09b649ce671\.artifacts\2022-08-24_22-34-20.fcsinputs.json --iterations=2 --warmups=1 --official 41.0.5 41.0.2 --local c:\projekty\fsharp\fsharp_main'
- Working directory: 'C:\projekty\fsharp\fsharp-benchmark-generator\bin\Release\net6.0\Benchmarks.Runner'.
[23:36:14 INF] Run:
[23:36:14 INF] Run: BenchmarkDotNet=v0.13.1, OS=Windows 10.0.22621
[23:36:14 INF] Run: AMD Ryzen 7 5700G with Radeon Graphics, 1 CPU, 16 logical and 8 physical cores
[23:36:14 INF] Run:   [Host]                         : .NET Framework 4.8 (4.8.9075.0), X64 LegacyJIT DEBUG
[23:36:14 INF] Run:   41.0.2                         : .NET Framework 4.8 (4.8.9075.0), X64 RyuJIT
[23:36:14 INF] Run:   41.0.5                         : .NET Framework 4.8 (4.8.9075.0), X64 RyuJIT
[23:36:14 INF] Run:   c:\projekty\fsharp\fsharp_main : .NET Framework 4.8 (4.8.9075.0), X64 RyuJIT
[23:36:14 INF] Run:
[23:36:14 INF] Run: EnvironmentVariables=FcsBenchmarkInput=C:\projekty\fsharp\fsharp-benchmark-generator\.artifacts\fantomas\0fe6785076e045f28e4c88e6a57dd09b649ce671\.artifacts\2022-08-24_22-34-20.fcsinputs.json  IterationCount=2  LaunchCount=1
[23:36:14 INF] Run: RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1
[23:36:14 INF] Run:
[23:36:14 INF] Run: | Method |                            Job |                                  NuGetReferences |    Mean | Error |  StdDev |       Gen 0 |       Gen 1 |     Gen 2 | Allocated |
[23:36:14 INF] Run: |------- |------------------------------- |------------------------------------------------- |--------:|------:|--------:|------------:|------------:|----------:|----------:|
[23:36:14 INF] Run: |    Run |                         41.0.2 | FSharp.Compiler.Service 41.0.2,FSharp.Core 6.0.2 | 12.35 s |    NA | 2.585 s | 692000.0000 | 134000.0000 | 7000.0000 |      4 GB |
[23:36:14 INF] Run: |    Run |                         41.0.5 | FSharp.Compiler.Service 41.0.5,FSharp.Core 6.0.5 | 12.09 s |    NA | 2.793 s | 704000.0000 | 141000.0000 | 7000.0000 |      4 GB |
[23:36:14 INF] Run: |    Run | c:\projekty\fsharp\fsharp_main | FSharp.Compiler.Service 41.0.6,FSharp.Core 6.0.6 | 12.25 s |    NA | 2.799 s | 698000.0000 | 140000.0000 | 7000.0000 |      4 GB |
[23:36:14 INF] Run: Full Log available in 'C:\projekty\fsharp\fsharp-benchmark-generator\bin\Release\net6.0\Benchmarks.Runner\BenchmarkDotNet.Artifacts\Benchmarks.Runner.FCSBenchmark-20220824-233422.log'
[23:36:14 INF] Run: Reports available in 'C:\projekty\fsharp\fsharp-benchmark-generator\bin\Release\net6.0\Benchmarks.Runner\BenchmarkDotNet.Artifacts\results'
```

</details>

## Benchmark description format
The benchmark description is a high-level definition of code analysis that we want to benchmark. It consists of two parts:
- a codebase to be analysed
- specific analysis actions (eg. analyse file `A.fs` in project `B`)

[inputs/](inputs/) directory contains existing samples.

Let's look at [inputs/fantomas.json](inputs/fantomas.json):
```json5
// Checkout a revision of Fantomas codebase and perform a single analysis action on the top-level file
{
  // Repository to checkout as input for code analysis benchmarking
  "Repo": {
    // Short name used for determining local checkout directory
    "Name": "fantomas",
    // Full URL to a publicy-available Git repository
    "GitUrl": "https://github.com/fsprojects/fantomas",
    // Revision to use for 'git checkout' - using a commit hash rather than a branch/tag name guarantees reproducability
    "Revision" : "724087e0ffe09c6e1db040fe81b9986d90be8cc6"
  },
  // Commands required to prepare a checked out codebase for code inspection with Ionide.ProjInfo
  // If not specified, defaults to a single `dotnet restore %slnpath%` command.
  "CodebasePrep": [
    {
      "Command": "dotnet",
      "Args": "tool restore"
    },
    {
      "Command": "dotnet",
      "Args": "paket restore"
    }
  ],
  // Solution to open relative to the repo's root - all projects in the solution will be available in action definitions below
  "SlnRelative": "fantomas.sln",
  // A sequence of actions to be performed by FCS on the above codebase
  "CheckActions": [
    // Analyse DaemonTests.fs in the project named Fantomas.Tests
    {
      "FileName": "Integration/DaemonTests.fs",
      // This is a project name only - not project file's path (we currently assume names are unique)
      "ProjectName": "Fantomas.Tests",
      // Run this action once
      "Repeat": 1
    }
  ]
}
```
#### Local codebase analysis benchmarks
Only for local testing purposes a benchmark can point to a local codebase to be analysed instead of a publicly available GitHub repo.

Since the specification provides no guarantees about the local codebase's contents, this mode should not be used for comparing results between machines/users (even if the same code is available locally on multiple machines).

See an example from [inputs/local_example.json](inputs/local_example.json): 
```json5
{
  // Path to the locally available codebase
  "LocalCodeRoot": "Path/To/Local/Code/Root",
  // The rest of the options are the same
  "SlnRelative": "solution.sln",
  "CheckActions": [
    {
      "FileName": "library.fs",
      "ProjectName": "root"
    }
  ]
}
```

## Known limitations
* For local FCS versions the FCS needs to be published (locally) by running `./build.cmd -noVisualStudio -pack` before the benchmark
* Depends on having the correct MSBuild/Dotnet setup available on the machine
* At the moment supports only one type of action (analysis of a file in a project)
