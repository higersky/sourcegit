using System.IO;

namespace SourceGit.Commands
{
    public class QueryGitDir : Command
    {
        public QueryGitDir(string workDir)
        {
            WorkingDirectory = workDir;
            Args = "rev-parse --git-dir";
        }

        public string GetResult()
        {
            return Parse(ReadToEnd());
        }

        private string Parse(Result rs)
        {
            if (!rs.IsSuccess)
                return null;

            var stdout = rs.StdOut.Trim();
            if (string.IsNullOrEmpty(stdout))
                return null;

            // `rev-parse --git-dir` may report an absolute Linux path when git runs
            // inside WSL. Convert it back to the Windows UNC form.
            stdout = FromGitPath(stdout);

            return Path.IsPathRooted(stdout) ? stdout : Path.GetFullPath(Path.Combine(WorkingDirectory, stdout));
        }
    }
}
