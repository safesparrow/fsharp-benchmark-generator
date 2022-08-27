/// Benchmarking a specific git revision of FCS
module FCSBenchmark.Generator.Git

open System.IO
open LibGit2Sharp

let clone (dir : string) (gitUrl : string) : Repository =
    if Directory.Exists dir then
        failwith $"{dir} already exists for code root"
    log.Verbose("Fetching '{gitUrl}' in '{dir}'", gitUrl, dir)
    Repository.Init(dir) |> ignore
    let repo = new Repository(dir)
    let remote = repo.Network.Remotes.Add("origin", gitUrl)
    repo.Network.Fetch(remote.Name, [])
    repo
    
let checkout (repo : Repository) (revision : string) : unit =
    log.Verbose("Checkout revision {revision} in {repo.Info.Path}", revision, repo.Info.Path)
    Commands.Checkout(repo, revision) |> ignore

let revisionDir (baseDir : string) (revision : string) =
    Path.Combine(baseDir, revision)

let prepareRevisionCheckout (baseDir : string) (repoUrl : string) (revision : string) =
    let dir = revisionDir baseDir revision
    if Repository.IsValid dir |> not then
        printfn $"Checking out revision {revision} in {dir}"
        try Directory.Delete dir with _ -> ()
        use repo = clone dir repoUrl
        checkout repo revision 
    else
        printfn $"{revision} already checked out in {dir}"

let private fcsDllPath (checkoutDir : string) =
    Path.Combine(checkoutDir, "artifacts/bin/FSharp.Compiler.Service/Release/netstandard2.0/FSharp.Compiler.Service.dll")
    
let private fsharpCoreDllPath (rootDir : string) =
    (fcsDllPath rootDir).Replace("FSharp.Compiler.Service.dll", "FSharp.Core.dll")

let checkoutContainsBuiltFcs (checkoutDir : string) =
    File.Exists(fcsDllPath checkoutDir)

let private prepareRevisionBuild (checkoutsBaseDir : string) (revision : string) =
    let dir = revisionDir checkoutsBaseDir revision
    if checkoutContainsBuiltFcs dir |> not then
        printfn $"'{fcsDllPath dir}' doesn't exist - building revision {revision} in {dir}..."
        Build.buildAndPackFCS dir
    else
        printfn $"{revision} already built in {dir}"

let checkoutAndBuild (checkoutsBaseDir : string) (revision : string) =
    prepareRevisionCheckout checkoutsBaseDir revision
    prepareRevisionBuild checkoutsBaseDir revision

let private prepareMainRepo (config : RunConfig) =
    let dir = revisionDir config.CheckoutBaseDir "main"
    if Directory.Exists dir then new Repository(dir)
    else clone dir

let findCommitsBetweenInclusive (config : RunConfig) (older : Commit) (newer : Commit) =
    let repo = prepareMainRepo config
    let filter : CommitFilter = CommitFilter(IncludeReachableFrom=newer, ExcludeReachableFrom=older)
    repo.Commits.QueryBy(filter)
    |> Seq.toList
    |> fun l ->
        if l |> List.contains older then l
        else l @ [older] 

let findCommit (config : RunConfig) (revision : string) =
    let repo = prepareMainRepo config
    repo.Lookup<Commit>(revision)    

let findCommitDate (config : RunConfig) (revision : string) =
    let commit = findCommit config revision
    commit.Committer.When
