using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRepositoryRootPath : Command
    {
        public QueryRepositoryRootPath(string path)
        {
            WorkingDirectory = path;
            Args = "rev-parse --show-toplevel";
        }

        public Result GetResult()
        {
            return FixupResult(ReadToEnd());
        }

        public async Task<Result> GetResultAsync()
        {
            return FixupResult(await ReadToEndAsync().ConfigureAwait(false));
        }

        private Result FixupResult(Result rs)
        {
            // `rev-parse --show-toplevel` reports a Linux path (e.g. `/home/dev/repo`)
            // when git runs inside WSL. Convert it back to the Windows UNC form.
            if (rs.IsSuccess && !string.IsNullOrEmpty(rs.StdOut))
                rs.StdOut = FromGitPath(rs.StdOut.Trim());

            return rs;
        }
    }
}
