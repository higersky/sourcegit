using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    /// <summary>
    ///     Utilities to make repositories stored inside WSL2 (accessed via `\\wsl$\` or
    ///     `\\wsl.localhost\` UNC paths) work seamlessly on Windows.
    ///
    ///     Git for Windows cannot operate on those paths correctly (dubious ownership,
    ///     9P round-trips, mangled symlinks/permissions, Linux hooks never run). So all
    ///     git invocations for such repositories are routed through `wsl.exe` and executed
    ///     by the git installed inside the corresponding distro.
    /// </summary>
    public static partial class WSL
    {
        private const string PREFIX_LOCALHOST = "\\\\wsl.localhost";
        private const string PREFIX_DOLLAR = "\\\\wsl$";
        private const string POLL_SEPARATOR = "--sourcegit-poll-separator--";
        /// <summary>
        ///     Indicates whether the given path points into a WSL2 distro.
        ///     Accepts both `\\wsl$\&lt;distro&gt;\...` and `\\wsl.localhost\&lt;distro&gt;\...`
        ///     with either separator style.
        /// </summary>
        /// <param name="path">Path to test.</param>
        /// <returns>True if the path is a WSL path.</returns>
        public static bool IsWSLPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
                return false;

            var normalized = path.Replace('/', '\\');
            return normalized.StartsWith(PREFIX_LOCALHOST, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(PREFIX_DOLLAR, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Try to parse the distro name from a WSL path.
        /// </summary>
        /// <param name="path">Path to test.</param>
        /// <param name="distro">Distro name.</param>
        /// <returns>True if a distro name can be resolved.</returns>
        public static bool TryGetDistro(string path, out string distro)
        {
            distro = null;
            if (!IsWSLPath(path))
                return false;

            var rest = StripServerPrefix(path.Replace('/', '\\'));
            rest = rest.TrimStart('\\');

            var idx = rest.IndexOf('\\');
            distro = idx < 0 ? rest : rest.Substring(0, idx);
            return !string.IsNullOrEmpty(distro);
        }

        /// <summary>
        ///     Normalize `\\wsl$\...` (or `//wsl$/...`) to the preferred `\\wsl.localhost\...`
        ///     form so all stored repository paths use a single canonical representation.
        /// </summary>
        /// <param name="path">Path to normalize.</param>
        /// <returns>Normalized path.</returns>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (!IsWSLPath(path))
                return path;

            var rest = StripServerPrefix(path.Replace('/', '\\'));
            return PREFIX_LOCALHOST + rest;
        }

        /// <summary>
        ///     Convert a WSL UNC path to the corresponding Linux path inside the distro.
        ///     e.g. `\\wsl.localhost\Ubuntu\home\dev\repo` → `/home/dev/repo`.
        /// </summary>
        /// <param name="windowsPath">WSL UNC path.</param>
        /// <returns>Linux path, or the input unchanged if it is not a WSL path.</returns>
        public static string ToLinuxPath(string windowsPath)
        {
            if (string.IsNullOrEmpty(windowsPath) || !IsWSLPath(windowsPath))
                return windowsPath;

            var rest = StripServerPrefix(windowsPath.Replace('/', '\\'));

            // Skip the distro name segment, the 9P share root maps to `/` inside the distro.
            rest = rest.TrimStart('\\');
            var sepIdx = rest.IndexOf('\\');
            rest = sepIdx < 0 ? string.Empty : rest.Substring(sepIdx + 1);
            rest = rest.Replace('\\', '/');
            return rest.Length == 0 ? "/" : "/" + rest;
        }

        /// <summary>
        ///     Strip the `\\wsl.localhost` / `\\wsl$` server prefix from a backslash-form
        ///     WSL path. The remainder still contains the distro segment.
        /// </summary>
        /// <param name="normalized">Backslash-form WSL path.</param>
        /// <returns>Path without the server prefix.</returns>
        private static string StripServerPrefix(string normalized)
        {
            return normalized.StartsWith(PREFIX_LOCALHOST, StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(PREFIX_LOCALHOST.Length)
                : normalized.Substring(PREFIX_DOLLAR.Length);
        }

        /// <summary>
        ///     Convert a Linux path inside a distro to the corresponding UNC path.
        ///     e.g. `/home/dev/repo` (distro `Ubuntu`) → `\\wsl.localhost\Ubuntu\home\dev\repo`.
        /// </summary>
        /// <param name="distro">Distro name.</param>
        /// <param name="linuxPath">Linux path.</param>
        /// <returns>UNC path, or the input unchanged if it is not a rooted Linux path.</returns>
        public static string ToUNCPath(string distro, string linuxPath)
        {
            if (string.IsNullOrEmpty(linuxPath) || linuxPath[0] != '/')
                return linuxPath;

            var rest = linuxPath.Substring(1).Replace('/', '\\');
            return string.IsNullOrEmpty(rest) ? $@"\\wsl.localhost\{distro}" : $@"\\wsl.localhost\{distro}\{rest}";
        }

        /// <summary>
        ///     Convert an absolute Windows path into the form the WSL side can access:
        ///     WSL UNC paths become Linux paths, drive paths become `/mnt/<drive>/...`.
        ///     Used for arguments passed to git running inside WSL (e.g. temp files
        ///     created by this app, or the app executable itself when used as editor).
        /// </summary>
        /// <param name="windowsPath">Absolute Windows path.</param>
        /// <returns>WSL-visible path, or the input unchanged when not convertible.</returns>
        public static string ToLinuxMountPath(string windowsPath)
        {
            if (string.IsNullOrEmpty(windowsPath))
                return windowsPath;

            if (IsWSLPath(windowsPath))
                return ToLinuxPath(windowsPath);

            if (windowsPath.Length >= 3 &&
                windowsPath[1] == ':' &&
                char.IsAsciiLetter(windowsPath[0]) &&
                (windowsPath[2] == '\\' || windowsPath[2] == '/'))
            {
                var rest = windowsPath.Substring(3).Replace('\\', '/');
                return rest.Length == 0 ? $"/mnt/{char.ToLowerInvariant(windowsPath[0])}" : $"/mnt/{char.ToLowerInvariant(windowsPath[0])}/{rest}";
            }

            return windowsPath;
        }

        /// <summary>
        ///     Translate an absolute Windows path argument for a git command which will be
        ///     executed inside WSL. Returns the input unchanged for non-WSL repositories.
        /// </summary>
        /// <param name="workDir">Repository path (Windows form).</param>
        /// <param name="path">Absolute path used as command argument.</param>
        /// <returns>Path in the form git inside WSL can access.</returns>
        public static string ToGitArgPath(string workDir, string path)
        {
            if (string.IsNullOrEmpty(path) || !TryGetDistro(workDir, out _))
                return path;

            return ToLinuxMountPath(path);
        }

        /// <summary>
        ///     Translate an absolute Linux path returned by git running inside WSL back to
        ///     the Windows UNC form. Returns the input unchanged for non-WSL repositories
        ///     or non-rooted-Linux values.
        /// </summary>
        /// <param name="workDir">Repository path (Windows form).</param>
        /// <param name="path">Path parsed from git output.</param>
        /// <returns>Windows UNC path.</returns>
        public static string FixupOutputPath(string workDir, string path)
        {
            if (string.IsNullOrEmpty(path) || !TryGetDistro(workDir, out var distro))
                return path;

            if (path[0] == '/')
                return ToUNCPath(distro, path);

            return path;
        }

        /// <summary>
        ///     Full path of `wsl.exe`.
        /// </summary>
        /// <returns>Executable file of WSL launcher.</returns>
        public static string Executable
        {
            get => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        }

        /// <summary>
        ///     Build the `wsl.exe` command line which runs git inside the distro owning
        ///     the given repository path. All arguments after `--exec` are passed to the
        ///     Linux process verbatim (no shell involved), so quoting rules of
        ///     `CommandLineToArgvW` apply.
        ///     NOTE: `wsl.exe` treats quotes in the `-d` value as part of the distro
        ///     name (its own option parser does not strip them), so the distro name
        ///     MUST be appended unquoted.
        /// </summary>
        /// <param name="workDir">Repository path in Windows form.</param>
        /// <param name="gitArgs">Arguments for git, as used for a local git executable.</param>
        /// <returns>Arguments for `wsl.exe`.</returns>
        public static string BuildGitCommandLine(string workDir, string gitArgs)
        {
            TryGetDistro(workDir, out var distro);
            var linuxWorkDir = ToLinuxPath(workDir);
            return $"-d {distro} --cd {linuxWorkDir.Quoted()} --exec git {gitArgs}";
        }

        /// <summary>
        ///     Append environment variables to `WSLENV` so that they are forwarded into
        ///     the WSL distro process (and back to Windows executables launched from it,
        ///     e.g. this app acting as git editor/askpass).
        /// </summary>
        /// <param name="environment">Target environment collection.</param>
        /// <param name="names">Variable names to forward.</param>
        public static void ForwardEnvironmentVariables(System.Collections.Generic.IDictionary<string, string> environment, params string[] names)
        {
            var existing = environment.TryGetValue("WSLENV", out var value) ? value : string.Empty;
            var builder = new StringBuilder(existing);
            foreach (var name in names)
            {
                if (builder.Length > 0)
                    builder.Append(':');
                builder.Append(name);
            }

            environment["WSLENV"] = builder.ToString();
        }

        /// <summary>
        ///     Query the git version installed in the given distro. Results are cached per distro.
        /// </summary>
        /// <param name="distro">Distro name.</param>
        /// <returns>Git version, or 0.0.0 when unavailable.</returns>
        public static Version GetGitVersion(string distro)
        {
            if (_versions.TryGetValue(distro, out var cached))
                return cached;

            lock (_lock)
            {
                if (_versions.TryGetValue(distro, out cached))
                    return cached;

                var start = new ProcessStartInfo();
                start.FileName = Executable;
                start.Arguments = $"-d {distro} --exec git --version"; // `-d` value must stay unquoted, see BuildGitCommandLine
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
                start.Environment.Add("WSL_UTF8", "1");

                var version = new Version(0, 0, 0);
                try
                {
                    using var proc = Process.Start(start)!;
                    var rs = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0)
                    {
                        var match = REG_GIT_VERSION().Match(rs);
                        if (match.Success)
                            version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
                    }
                }
                catch
                {
                    // Ignore. Distros without git installed simply report 0.0.0.
                }

                _versions[distro] = version;
                return version;
            }
        }

        /// <summary>
        ///     Poll the state of a repository inside WSL in a single `wsl.exe`
        ///     invocation: working copy status (`--porcelain=v2 --branch`) followed by
        ///     all branch/tag/stash refs. Used as the watcher fallback when no inotify
        ///     helper can run inside the distro.
        /// </summary>
        /// <param name="workDir">Repository path in Windows form.</param>
        /// <returns>Status output and refs output, or null parts when the poll failed.</returns>
        public static async Task<(string Status, string Refs)> PollRepositoryStateAsync(string workDir)
        {
            if (!TryGetDistro(workDir, out var distro))
                return (null, null);

            var payload = $"git --no-optional-locks status --porcelain=v2 --branch; " +
                $"printf '\\n{POLL_SEPARATOR}\\n'; " +
                $"git for-each-ref --format='%(refname) %(objectname)' refs/heads refs/tags refs/stash";
            var rs = await RunCaptureAsync(start =>
            {
                start.Arguments = $"-d {distro} --cd {ToLinuxPath(workDir).Quoted()} --exec sh -c {payload.Quoted()}";
            }).ConfigureAwait(false);

            if (rs == null)
                return (null, null);

            var sepIdx = rs.IndexOf(POLL_SEPARATOR, StringComparison.Ordinal);
            if (sepIdx < 0)
                return (null, null);

            var status = rs.Substring(0, sepIdx).Trim();
            var refs = rs.Substring(sepIdx + POLL_SEPARATOR.Length).Trim();
            return (string.IsNullOrEmpty(status) ? string.Empty : status, string.IsNullOrEmpty(refs) ? string.Empty : refs);
        }

        /// <summary>
        ///     Try to detect a usable SSH agent socket inside the given distro, so that
        ///     git network commands can reuse it. Checks the inherited environment, the
        ///     keychain environment files, and the well-known systemd user sockets.
        ///     Positive results are cached for the whole session, negative ones for a
        ///     short period (the agent may be started later).
        /// </summary>
        /// <param name="distro">Distro name.</param>
        /// <returns>Socket path inside the distro, or empty when no agent was found.</returns>
        public static string GetSSHAgentSocket(string distro)
        {
            if (_agentSockets.TryGetValue(distro, out var cached) &&
                (cached.Socket != null || DateTime.UtcNow - cached.ProbedAt < AGENT_PROBE_RETRY_INTERVAL))
            {
                return cached.Socket ?? string.Empty;
            }

            lock (_agentLock)
            {
                if (_agentSockets.TryGetValue(distro, out cached) &&
                    (cached.Socket != null || DateTime.UtcNow - cached.ProbedAt < AGENT_PROBE_RETRY_INTERVAL))
                {
                    return cached.Socket ?? string.Empty;
                }

                var socket = RunCaptureAsync(start =>
                {
                    start.Arguments = $"-d {distro} --exec sh -c {FIND_AGENT_SCRIPT.Quoted()}"; // `-d` value must stay unquoted, see BuildGitCommandLine
                }).ConfigureAwait(false).GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(socket))
                    socket = null;

                _agentSockets[distro] = (socket, DateTime.UtcNow);
                return socket ?? string.Empty;
            }
        }

        /// <summary>
        ///     Start a long-lived file event watcher inside the distro owning the given
        ///     repository path. It streams one changed path per stdout line, using
        ///     `inotifywait` when available and an embedded python3 inotify script
        ///     otherwise. The helper terminates by itself when its stdin is closed,
        ///     which happens as soon as this application exits — even when it crashes
        ///     or is killed without disposing the watcher.
        /// </summary>
        /// <param name="workDir">Repository path in Windows form.</param>
        /// <param name="onEvent">Called on the watcher thread for each changed path (Linux form, relative to repository root).</param>
        /// <param name="onExited">Called when the watcher process exited or its stream closed; the argument
        /// is the process exit code when already known (null otherwise). Code 3 means the distro has
        /// neither `inotifywait` nor `python3` — a deterministic failure rather than a crash.</param>
        /// <returns>The watcher process, or null when it could not be started.</returns>
        public static Process StartFileEventWatcher(string workDir, Action<string> onEvent, Action<int?> onExited)
        {
            if (!TryGetDistro(workDir, out var distro))
                return null;

            var pythonScript = TryDeployInotifyPythonScript(distro);
            var start = new ProcessStartInfo();
            start.FileName = Executable;
            start.Arguments = $"-d {distro} --cd {ToLinuxPath(workDir).Quoted()} --exec sh -c {BuildInotifyScript(pythonScript).Quoted()}";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;    // kept open by the app; closing it (incl. app crash) makes the helper self-terminate
            start.RedirectStandardOutput = true;   // event stream
            start.RedirectStandardError = false;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.Environment.Add("WSL_UTF8", "1");

            var proc = new Process();
            proc.StartInfo = start;
            proc.EnableRaisingEvents = true;
            int? exitedWith = null;
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 })
                    onEvent(e.Data);
            };

            // The helper script always terminates its process when its streams end,
            // so `Exited` is the reliable single exit signal — and unlike the
            // stream-close notification it carries the exit code without a race.
            proc.Exited += (_, _) =>
            {
                try
                {
                    exitedWith = proc.ExitCode;
                }
                catch
                {
                    // Process object already released.
                }

                onExited(exitedWith);
            };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                return proc;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Build the command line git uses to launch an external diff/merge tool
        ///     for a WSL repository: the tool executable is converted to its
        ///     `/mnt/<drive>/...` form (launched via WSL interop), and every path
        ///     placeholder is wrapped with `wslpath -w` so the Windows tool receives
        ///     `\\wsl.localhost\...` UNC paths (e.g. VS Code opens those natively).
        /// </summary>
        /// <param name="exec">Tool executable in Windows form.</param>
        /// <param name="cmd">Tool command template containing $LOCAL/$REMOTE/$BASE/$MERGED.</param>
        /// <returns>Command template usable by git running inside WSL.</returns>
        public static string BuildExternalToolCmd(string exec, string cmd)
        {
            foreach (var name in TOOL_PATH_VARIABLES)
                cmd = cmd.Replace($"${name}", $"$(wslpath -w \"${name}\")");

            return $"{ToLinuxMountPath(exec).Quoted()} {cmd}";
        }

        /// <summary>
        ///     Core runner for captured `wsl.exe` invocations.
        /// </summary>
        /// <param name="configure">Callback which fills in the `wsl.exe` arguments.</param>
        /// <returns>Trimmed stdout, or null when the command failed.</returns>
        private static async Task<string> RunCaptureAsync(Action<ProcessStartInfo> configure)
        {
            var start = new ProcessStartInfo();
            start.FileName = Executable;
            configure(start);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.Environment.Add("WSL_UTF8", "1");

            try
            {
                using var proc = Process.Start(start)!;
                var rs = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                return proc.ExitCode == 0 ? rs.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Build the shell script executed by `StartFileEventWatcher`.
        /// </summary>
        /// <returns>POSIX shell script printing one changed path per line.</returns>
        private static string BuildInotifyScript(string pythonScript)
        {
            // `inotifywait` branch: stream events in the background. The foreground
            // `wait` returns when `inotifywait` dies, and a background reader sends
            // TERM once stdin is closed (parent gone) — either way all streams are
            // cleaned up, so the helper never lingers, and the app restarts it.
            // Repositories whose `.git/refs` is a symlink (e.g. Android `repo` tool
            // checkouts) get an additional watch on the resolved directory, since
            // neither inotify nor the 9P share follow symlinks; `sed` maps the events
            // back to their `.git/refs/...` paths.
            // `python3` branch: runs the watcher script that `TryDeployInotifyPythonScript`
            // deployed as a real file inside the distro — plainly visible in `ps`
            // instead of a base64 blob on the command line. Omitted when deployment
            // failed; the caller then falls back to polling.
            var python = string.IsNullOrEmpty(pythonScript)
                ? string.Empty
                : $"elif command -v python3 >/dev/null 2>&1 && [ -r {pythonScript} ]; then\n    exec python3 {pythonScript}\n";

            // Raw string literals keep the line endings of the source file, which may
            // be CRLF depending on editor/git configuration. `sh` does not treat CR
            // as whitespace, so normalize to LF before handing the script to the distro.
            var script = $"""
                if command -v inotifywait >/dev/null 2>&1; then
                    trap 'kill $W $W2 2>/dev/null; exit 0' TERM
                    inotifywait -m -r -q -e modify,create,delete,move --format '%w%f' . &
                    W=$!
                    W2=
                    R=$(readlink -f ./.git/refs 2>/dev/null)
                    if [ -n "$R" ] && [ -d "$R" ] && [ "$R" != "$PWD/.git/refs" ]; then
                        inotifywait -m -r -q -e modify,create,delete,move --format '%w%f' "$R" | sed -u "s|^$R/|.git/refs/|" &
                        W2=$!
                    fi
                    exec 3<&0
                    ( read _ <&3; kill -TERM $$ ) &
                    wait $W
                    kill $! $W2 2>/dev/null
                    exit 0
                {python}else
                    exit 3
                fi
                """;

            return script.Replace("\r\n", "\n");
        }

        /// <summary>
        ///     Write the fallback python watcher to a file inside the distro, so the
        ///     helper process shows a readable command line in `ps` (a base64 blob on
        ///     the command line looks like obfuscation). The file name is derived from
        ///     a hash of the script content, so different application versions never
        ///     fight over the same file; stale files disappear when the distro restarts
        ///     (`wsl --shutdown` clears `/tmp`).
        /// </summary>
        /// <param name="distro">Distro name.</param>
        /// <returns>Linux path of the deployed script, or null when it could not be written.</returns>
        private static string TryDeployInotifyPythonScript(string distro)
        {
            try
            {
                File.WriteAllText(ToUNCPath(distro, INOTIFY_PYTHON_FILE), INOTIFY_PYTHON_SOURCE.Replace("\r\n", "\n"));
                return INOTIFY_PYTHON_FILE;
            }
            catch
            {
                return null;
            }
        }

        // Fallback inotify watcher for distros without `inotify-tools`. Watches the
        // repository recursively via ctypes syscalls, prints one changed path per line
        // (same format as `inotifywait --format '%w%f'`), watches newly created
        // directories, follows a symlinked `.git/refs` (Android `repo` tool checkouts)
        // by watching the resolved directory and mapping events back to their
        // `.git/refs/...` paths, and exits when stdin reaches EOF (parent is gone).
        private const string INOTIFY_PYTHON_SOURCE = """
            import ctypes, os, select, struct, sys
            libc = ctypes.CDLL(None, use_errno=True)
            libc.inotify_init1.restype = ctypes.c_int
            libc.inotify_add_watch.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_uint32]
            libc.inotify_add_watch.restype = ctypes.c_int
            IN_MODIFY = 2
            IN_MOVED_FROM = 64
            IN_MOVED_TO = 128
            IN_CREATE = 256
            IN_DELETE = 512
            IN_ISDIR = 1073741824
            MASK = IN_MODIFY | IN_MOVED_FROM | IN_MOVED_TO | IN_CREATE | IN_DELETE
            fd = libc.inotify_init1(2048)
            if fd < 0:
                sys.exit(3)
            watched = {}
            def add_watch(real, display):
                try:
                    wd = libc.inotify_add_watch(fd, real.encode(), MASK)
                except Exception:
                    return
                if wd > 0:
                    watched[wd] = (real, display)
            def watch_tree(root, display):
                if not os.path.isdir(root):
                    return
                for r, dirs, files in os.walk(root):
                    rel = os.path.relpath(r, root)
                    add_watch(r, display if rel == u'.' else display + u'/' + rel)
            watch_tree(u'.', u'.')
            real_refs = os.path.realpath(u'.git/refs')
            if real_refs != os.path.abspath(u'.git/refs'):
                watch_tree(real_refs, u'.git/refs')
            poller = select.poll()
            poller.register(fd, select.POLLIN)
            poller.register(0, select.POLLIN)
            while True:
                for fdno, _ in poller.poll(2000):
                    if fdno == 0:
                        if not os.read(0, 4096):
                            sys.exit(0)
                        continue
                    try:
                        buf = os.read(fd, 262144)
                    except OSError:
                        continue
                    pos = 0
                    out = []
                    while pos + 16 <= len(buf):
                        wd, mask, cookie, namelen = struct.unpack_from(b'iIII', buf, pos)
                        name = buf[pos + 16:pos + 16 + namelen].split(b'\x00')[0].decode('utf-8', 'replace')
                        pos += 16 + namelen
                        real, display = watched.get(wd, (u'.', u'.'))
                        if name:
                            path = display + u'/' + name
                            realpath = real + u'/' + name
                        else:
                            path = display
                            realpath = real
                        if (mask & IN_ISDIR) and (mask & IN_CREATE):
                            add_watch(realpath, path)
                        out.append(path)
                    if out:
                        sys.stdout.write(u'\n'.join(out) + u'\n')
                        sys.stdout.flush()
            """;

        // Path of the deployed python watcher inside the distro. Versioned by content
        // hash so several application versions can coexist without clobbering each
        // other; `/tmp` is wiped whenever the distro restarts.
        private static readonly string INOTIFY_PYTHON_FILE =
            $"/tmp/sourcegit-inotify-watcher-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(INOTIFY_PYTHON_SOURCE))).Substring(0, 8).ToLowerInvariant()}.py";

        // Detects a usable SSH agent socket. Runs non-interactively, so shell rc files
        // are NOT sourced: check the inherited env, keychain env files, then systemd.
        private const string FIND_AGENT_SCRIPT =
            "if [ -n \"$SSH_AUTH_SOCK\" ] && [ -S \"$SSH_AUTH_SOCK\" ]; then printf %s \"$SSH_AUTH_SOCK\"; exit 0; fi; " +
            "for f in \"$HOME\"/.keychain/*-sh; do [ -f \"$f\" ] && . \"$f\"; done; " +
            "if [ -n \"$SSH_AUTH_SOCK\" ] && [ -S \"$SSH_AUTH_SOCK\" ]; then printf %s \"$SSH_AUTH_SOCK\"; exit 0; fi; " +
            "for s in \"$XDG_RUNTIME_DIR/ssh-auth-sock\" \"$XDG_RUNTIME_DIR/keyring/ssh\"; do " +
            "if [ -S \"$s\" ]; then printf %s \"$s\"; exit 0; fi; done";

        private static readonly string[] TOOL_PATH_VARIABLES = ["MERGED", "REMOTE", "BASE", "LOCAL"];
        private static readonly TimeSpan AGENT_PROBE_RETRY_INTERVAL = TimeSpan.FromSeconds(60);
        private static readonly ConcurrentDictionary<string, (string Socket, DateTime ProbedAt)> _agentSockets = new();
        private static readonly object _agentLock = new();

        [GeneratedRegex(@"^git version[\s\w]*(\d+)\.(\d+)[\.\-](\d+).*$")]
        private static partial Regex REG_GIT_VERSION();

        private static readonly ConcurrentDictionary<string, Version> _versions = new();
        private static readonly object _lock = new();
    }
}
