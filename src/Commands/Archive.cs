namespace SourceGit.Commands
{
    public class Archive : Command
    {
        public Archive(string repo, string revision, string saveTo)
        {
            WorkingDirectory = repo;
            Context = repo;
            saveTo = ToGitPath(saveTo);
            Args = $"archive --format=zip --verbose --output={saveTo.Quoted()} {revision}";
        }
    }
}
