using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        public string Args { get; set; } = string.Empty;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;

        public async Task<bool> ExecAsync()
        {
            Log?.AppendLine($"$ git {Args}\n");

            var errs = new List<string>();

            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);
            proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
            proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

            var captured = new CapturedProcess() { Process = proc };
            var capturedLock = new object();
            try
            {
                proc.Start();

                // Not safe, please only use `CancellationToken` in readonly commands.
                if (CancellationToken.CanBeCanceled)
                {
                    CancellationToken.Register(() =>
                    {
                        lock (capturedLock)
                        {
                            if (captured is { Process: { HasExited: false } })
                                Native.OS.TerminateProcess(captured.Process);
                        }
                    });
                }
            }
            catch (Exception e)
            {
                if (RaiseError)
                    RaiseException(e.Message);

                Log?.AppendLine(string.Empty);
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HandleOutput(e.Message, errs);
            }

            lock (capturedLock)
            {
                captured.Process = null;
            }

            Log?.AppendLine(string.Empty);

            if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
            {
                if (RaiseError)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    if (!string.IsNullOrEmpty(errMsg))
                        RaiseException(errMsg);
                }

                return false;
            }

            return true;
        }

        protected Result ReadToEnd()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = proc.StandardOutput.ReadToEnd();
            rs.StdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected async Task<Result> ReadToEndAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            rs.StdOut = await proc.StandardOutput.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            rs.StdErr = await proc.StandardError.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);

            rs.IsSuccess = proc.ExitCode == 0;
            return rs;
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var isWSL = Native.WSL.IsWSLPath(WorkingDirectory);
            var useSetSid = CancellationToken.CanBeCanceled && Native.OS.SupportSetSid();
            var selfExecFile = Environment.ProcessPath;
            var builder = new StringBuilder(2048);

            if (useSetSid)
                builder.Append(Native.OS.GitExecutable.Quoted()).Append(' ');

            builder.Append("--no-pager -c core.quotepath=off ");

            // For repositories inside WSL, git is executed by the distro itself. Do NOT
            // force a Windows credential helper there — the WSL side gitconfig decides
            // (commonly `git-credential-manager` installed inside WSL).
            if (!isWSL)
                builder.Append("-c credential.helper=").Append(Native.OS.CredentialHelper).Append(' ');

            // When git runs inside WSL it cannot invoke a Windows executable by its Windows
            // path, so point editor settings to the `/mnt/<drive>/...` form instead. WSL
            // interop then brings the app up on the Windows side when git needs an editor.
            var editorExecFile = isWSL ? Native.WSL.ToLinuxMountPath(selfExecFile) : selfExecFile;

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{editorExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{editorExecFile}\" --rebase-message-editor" -c sequence.editor="\"{editorExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);

            var start = new ProcessStartInfo();
            if (isWSL)
            {
                start.FileName = Native.WSL.Executable;
                start.Arguments = Native.WSL.BuildGitCommandLine(WorkingDirectory, builder.ToString());
            }
            else
            {
                start.FileName = useSetSid ? Native.OS.GetSetSidExecutable() : Native.OS.GitExecutable;
                start.Arguments = builder.ToString();
            }

            start.UseShellExecute = false;
            start.CreateNoWindow = true;

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            if (isWSL)
            {
                var distro = GetDistroName();

                // Force using this app as SSH askpass program. `WSLENV` forwards the
                // variables into the distro (`/p` also translates the askpass path into
                // its `/mnt/<drive>/...` form), and carries them back into this app when
                // git inside WSL launches it as editor/askpass via interop.
                start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`
                start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
                start.Environment.Add("SOURCEGIT_LAUNCH_AS_ASKPASS", "TRUE");
                start.Environment.Add("SOURCEGIT_WSL_DISTRO", distro);
                start.Environment.Add("WSL_UTF8", "1");

                var forwarded = new List<string> { "SSH_ASKPASS/p", "SSH_ASKPASS_REQUIRE", "SOURCEGIT_LAUNCH_AS_ASKPASS", "SOURCEGIT_WSL_DISTRO" };

                // Reuse an SSH agent running inside the distro (e.g. keychain or a
                // systemd user service) when one can be detected.
                var agentSocket = Native.WSL.GetSSHAgentSocket(distro);
                if (!string.IsNullOrEmpty(agentSocket))
                {
                    start.Environment.Add("SSH_AUTH_SOCK", agentSocket);
                    forwarded.Add("SSH_AUTH_SOCK");
                }

                // If an SSH private key was provided, sets the environment.
                if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                {
                    start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{Native.WSL.ToGitArgPath(WorkingDirectory, SSHKey)}' -o AddKeysToAgent=yes");
                    forwarded.Add("GIT_SSH_COMMAND");
                }

                // Force using English locale (UTF-8) for stable output parsing.
                start.Environment.Add("LANG", "C.UTF-8");
                start.Environment.Add("LC_ALL", "C.UTF-8");
                forwarded.Add("LANG");
                forwarded.Add("LC_ALL");

                Native.WSL.ForwardEnvironmentVariables(start.Environment, [.. forwarded]);
            }
            else
            {
                // Force using this app as SSH askpass program
                start.Environment.Add("SSH_ASKPASS", selfExecFile); // Can not use parameter here, because it invoked by SSH with `exec`
                start.Environment.Add("SSH_ASKPASS_REQUIRE", "prefer");
                start.Environment.Add("SOURCEGIT_LAUNCH_AS_ASKPASS", "TRUE");
                if (!OperatingSystem.IsLinux())
                    start.Environment.Add("DISPLAY", "required");

                // If an SSH private key was provided, sets the environment.
                if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
                    start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{SSHKey}' -o AddKeysToAgent=yes");

                // Force using en_US.UTF-8 locale
                if (OperatingSystem.IsLinux())
                {
                    start.Environment.Add("LANG", "C");
                    start.Environment.Add("LC_ALL", "C");
                }
            }

            // Working directory
            if (!isWSL && !string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        /// <summary>
        ///     Convert an absolute path argument into the form git can access for the
        ///     repository this command operates on. Identity for normal repositories.
        /// </summary>
        /// <param name="path">Absolute path.</param>
        /// <returns>Git-visible path.</returns>
        protected string ToGitPath(string path)
        {
            return Native.OS.GetPathForGit(WorkingDirectory, path);
        }

        /// <summary>
        ///     Convert an absolute path reported by git into the native form for the
        ///     repository this command operates on. Identity for normal repositories.
        /// </summary>
        /// <param name="path">Path parsed from git output.</param>
        /// <returns>Native path.</returns>
        protected string FromGitPath(string path)
        {
            return Native.OS.FixupPathFromGit(WorkingDirectory, path);
        }

        /// <summary>
        ///     Build a `ProcessStartInfo` for running git (or `wsl.exe` → git inside the
        ///     distro, when the given workdir is a WSL path) with the given arguments.
        ///     Used by code paths which cannot go through `ExecAsync`/`ReadToEnd`, e.g.
        ///     commands that stream binary data or feed stdin.
        /// </summary>
        /// <param name="workDir">Repository path in Windows form.</param>
        /// <param name="args">Full git argument string.</param>
        /// <returns>Pre-configured process start info.</returns>
        internal static ProcessStartInfo CreateGitProcessStartInfo(string workDir, string args)
        {
            var isWSL = Native.WSL.IsWSLPath(workDir);
            var start = new ProcessStartInfo();

            if (isWSL)
            {
                start.FileName = Native.WSL.Executable;
                start.Arguments = Native.WSL.BuildGitCommandLine(workDir, args);
            }
            else
            {
                start.FileName = Native.OS.GitExecutable;
                start.Arguments = args;
                start.WorkingDirectory = workDir;
            }

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;

            if (isWSL)
            {
                start.Environment.Add("WSL_UTF8", "1");
                start.Environment.Add("LANG", "C.UTF-8");
                start.Environment.Add("LC_ALL", "C.UTF-8");
                Native.WSL.ForwardEnvironmentVariables(start.Environment, ["LANG", "LC_ALL"]);
            }

            return start;
        }

        private string GetDistroName()
        {
            return Native.WSL.TryGetDistro(WorkingDirectory, out var distro) ? distro : string.Empty;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
