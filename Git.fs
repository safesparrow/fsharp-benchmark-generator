/// Benchmarking a specific git revision of FCS
module FCSBenchmark.Generator.Git

open System.IO
open LibGit2Sharp

let clone (dir : string) (gitUrl : string) : Repository =
    if Directory.Exists dir then
        failwith $"{dir} already exists for code root"

    log.Verbose ("Fetching '{gitUrl}' in '{dir}'", gitUrl, dir)
    Repository.Init (dir) |> ignore
    let repo = new Repository (dir)
    let remote = repo.Network.Remotes.Add ("origin", gitUrl)
    repo.Network.Fetch (remote.Name, [])
    repo

let checkout (repo : Repository) (revision : string) : unit =
    log.Verbose ("Checkout revision {revision} in {repo.Info.Path}", revision, repo.Info.Path)
    Commands.Checkout (repo, revision) |> ignore

let revisionDir (baseDir : string) (revision : string) = Path.Combine (baseDir, revision)

let prepareRevisionCheckout (baseDir : string) (repoUrl : string) (revision : string) =
    let dir = revisionDir baseDir revision

    if Repository.IsValid dir |> not then
        printfn $"Checking out revision {revision} in {dir}"

        try
            Directory.Delete dir
        with _ ->
            ()

        use repo = clone dir repoUrl
        checkout repo revision
    else
        printfn $"{revision} already checked out in {dir}"
