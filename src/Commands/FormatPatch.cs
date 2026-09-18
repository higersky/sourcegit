namespace SourceGit.Commands
{
    public class FormatPatch : Command
    {
        public FormatPatch(string repo, string commit, string saveTo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Editor = EditorType.None;
            saveTo = ToGitPath(saveTo);
            Args = $"format-patch {commit} -1 --output={saveTo.Quoted()}";
        }
    }
}
