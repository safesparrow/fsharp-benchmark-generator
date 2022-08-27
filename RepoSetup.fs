/// Preparing a codebase based on a 'RepoSpec'
module FCSBenchmark.Generator.RepoSetup

open System.IO
open LibGit2Sharp

[<CLIMutable>]
type RepoSpec =
    {
        Name : string
        GitUrl : string
        Revision : string
    }
    with override this.ToString() = $"{this.Name} ({this.GitUrl}) @ {this.Revision}"
    
type Config =
    {
        BaseDir : string
    }

let revisionDir (config : Config) (spec : RepoSpec) =
    Path.Combine(config.BaseDir, spec.Name, spec.Revision)

let prepare (config : Config) (spec : RepoSpec) =
    log.Information("Preparing repo {spec}", spec)
    let dir = revisionDir config spec
    if Repository.IsValid dir |> not then
        use repo = Git.clone dir spec.GitUrl
        Git.checkout repo spec.Revision
        repo
    else
        log.Information("{dir} already exists - will assume the correct repository is already checked out", dir)
        new Repository(dir)