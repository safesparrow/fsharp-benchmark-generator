module FCSBenchmark.Generator

open System
open System.IO

type RunConfig =
    {
        /// Used to identify a set of BDN artifacts - set to a unique value unless you want to reuse (partial) results from previous runs
        Time : DateTime
        /// Used to store checkouts and BDN artifacts - set to a valid local absolute path
        BaseDir : string
        /// How many revisions should be checked out and built in parallel
        Parallelism : int
        /// Name to suffx the benchmark result files with
        ResultsSuffix : string
        /// Whether to build local codebases before benchmarking
        BuildLocalCodebases : bool
    }
    with
        member this.CheckoutBaseDir = Path.Combine(this.BaseDir, "checkouts")
        member this.BDNOutputBaseDir = Path.Combine(this.BaseDir, "bdns")

module RunConfig =
    let makeDefault () =
        {
            Time = DateTime.UtcNow
            BaseDir = "."
            Parallelism = 1
            ResultsSuffix = "results"
            BuildLocalCodebases = false
        }
