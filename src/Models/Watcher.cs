using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class Watcher : IDisposable
    {
        private const int WSL_POLL_INITIAL_DELAY_MS = 2000;
        private const int WSL_POLL_INTERVAL_ACTIVE_MS = 30000;
        private const int WSL_POLL_INTERVAL_INACTIVE_MS = 300000;
        private const int WSL_RESTART_BASE_DELAY_MS = 500;
        private const int WSL_RESTART_MAX_DELAY_MS = 30000;
        private const int WSL_RESTART_MAX_ATTEMPTS = 5;
        private const long WSL_WATCHER_STABLE_TICKS = 10 * TimeSpan.TicksPerSecond;

        public class LockContext : IDisposable
        {
            public LockContext(Watcher target)
            {
                _target = target;
                Interlocked.Increment(ref _target._lockCount);
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref _target._lockCount);
            }

            private Watcher _target;
        }

        public Watcher(IRepository repo, string fullpath, string gitDir)
        {
            _repo = repo;
            _root = new DirectoryInfo(fullpath).FullName;
            _watchers = new List<FileSystemWatcher>();

            // FileSystemWatcher cannot receive change notifications from WSL distros
            // (the 9P file share does not implement them). Use a long-lived inotify
            // helper inside the distro instead, which terminates itself when this
            // application goes away. Distros without the required tooling settle into
            // polling directly. An unexpected helper death triggers a full refresh and
            // an exponential-backoff restart (a stable run resets the attempt counter);
            // after repeated quick failures the watcher settles into polling for good.
            if (Native.WSL.IsWSLPath(fullpath))
            {
                _timer = new Timer(Tick, null, 100, 100);
                Task.Run(StartWSLEventWatcher);
                return;
            }

            var testGitDir = new DirectoryInfo(Path.Combine(fullpath, ".git")).FullName;
            var desiredDir = new DirectoryInfo(gitDir).FullName;
            if (testGitDir.Equals(desiredDir, StringComparison.Ordinal))
            {
                var combined = new FileSystemWatcher();
                combined.Path = fullpath;
                combined.Filter = "*";
                combined.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                combined.IncludeSubdirectories = true;
                combined.Created += OnRepositoryChanged;
                combined.Renamed += OnRepositoryChanged;
                combined.Changed += OnRepositoryChanged;
                combined.Deleted += OnRepositoryChanged;
                combined.EnableRaisingEvents = false;

                _watchers.Add(combined);
            }
            else
            {
                var wc = new FileSystemWatcher();
                wc.Path = fullpath;
                wc.Filter = "*";
                wc.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                wc.IncludeSubdirectories = true;
                wc.Created += OnWorkingCopyChanged;
                wc.Renamed += OnWorkingCopyChanged;
                wc.Changed += OnWorkingCopyChanged;
                wc.Deleted += OnWorkingCopyChanged;
                wc.EnableRaisingEvents = false;

                var git = new FileSystemWatcher();
                git.Path = gitDir;
                git.Filter = "*";
                git.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                git.IncludeSubdirectories = true;
                git.Created += OnGitDirChanged;
                git.Renamed += OnGitDirChanged;
                git.Changed += OnGitDirChanged;
                git.Deleted += OnGitDirChanged;
                git.EnableRaisingEvents = false;

                _watchers.Add(wc);
                _watchers.Add(git);
            }

            _timer = new Timer(Tick, null, 100, 100);

            // Starts filesystem watchers in another thread to avoid UI blocking
            Task.Run(() =>
            {
                try
                {
                    foreach (var watcher in _watchers)
                        watcher.EnableRaisingEvents = true;
                }
                catch
                {
                    // Ignore exceptions. This may occur while `Dispose` is called.
                }
            });
        }

        public IDisposable Lock()
        {
            return new LockContext(this);
        }

        /// <summary>
        ///     Notify the watcher whether the main window is focused. Only affects the
        ///     WSL poll fallback: poll rarely while the user is not looking at the app,
        ///     and refresh immediately once focus returns.
        /// </summary>
        /// <param name="focused">Whether the main window has focus.</param>
        public void SetWindowFocused(bool focused)
        {
            _windowFocused = focused;

            if (_pollTimer != null)
            {
                var period = focused ? WSL_POLL_INTERVAL_ACTIVE_MS : WSL_POLL_INTERVAL_INACTIVE_MS;
                _pollTimer.Change(focused ? 0 : period, period);
            }
        }

        public void MarkBranchUpdated()
        {
            Interlocked.Exchange(ref _updateBranch, 0);
            Interlocked.Exchange(ref _updateWC, 0);
        }

        public void MarkTagUpdated()
        {
            Interlocked.Exchange(ref _updateTags, 0);
        }

        public void MarkWorkingCopyUpdated()
        {
            Interlocked.Exchange(ref _updateWC, 0);
        }

        public void MarkStashUpdated()
        {
            Interlocked.Exchange(ref _updateStashes, 0);
        }

        public void MarkSubmodulesUpdated()
        {
            Interlocked.Exchange(ref _updateSubmodules, 0);
        }

        public void Dispose()
        {
            _disposed = true;

            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
            }

            if (_wslRestartTimer != null)
            {
                _wslRestartTimer.Dispose();
                _wslRestartTimer = null;
            }

            var eventWatcher = Interlocked.Exchange(ref _eventWatcher, null);
            try
            {
                if (eventWatcher is { HasExited: false })
                    eventWatcher.Kill();
                eventWatcher?.Dispose();
            }
            catch
            {
                // Ignore. The helper also terminates itself when its stdin closes.
            }

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
            _timer.Dispose();
            _timer = null;
        }

        private void Tick(object sender)
        {
            if (Interlocked.Read(ref _lockCount) > 0)
                return;

            var now = DateTime.Now.ToFileTime();
            var refreshCommits = false;
            var refreshSubmodules = false;
            var refreshWC = false;

            var oldUpdateBranch = Interlocked.Exchange(ref _updateBranch, -1);
            if (oldUpdateBranch > 0)
            {
                if (now > oldUpdateBranch)
                {
                    refreshCommits = true;
                    refreshSubmodules = _repo.MayHaveSubmodules();
                    refreshWC = true;

                    _repo.RefreshBranches();
                    _repo.RefreshWorktrees();
                }
                else
                {
                    Interlocked.CompareExchange(ref _updateBranch, oldUpdateBranch, -1);
                }
            }

            if (refreshWC)
            {
                Interlocked.Exchange(ref _updateWC, -1);
                _repo.RefreshWorkingCopyChanges();
            }
            else
            {
                var oldUpdateWC = Interlocked.Exchange(ref _updateWC, -1);
                if (oldUpdateWC > 0)
                {
                    if (now > oldUpdateWC)
                        _repo.RefreshWorkingCopyChanges();
                    else
                        Interlocked.CompareExchange(ref _updateWC, oldUpdateWC, -1);
                }
            }

            if (refreshSubmodules)
            {
                Interlocked.Exchange(ref _updateSubmodules, -1);
                _repo.RefreshSubmodules();
            }
            else
            {
                var oldUpdateSubmodule = Interlocked.Exchange(ref _updateSubmodules, -1);
                if (oldUpdateSubmodule > 0)
                {
                    if (now > oldUpdateSubmodule)
                        _repo.RefreshSubmodules();
                    else
                        Interlocked.CompareExchange(ref _updateSubmodules, oldUpdateSubmodule, -1);
                }
            }

            var oldUpdateStashes = Interlocked.Exchange(ref _updateStashes, -1);
            if (oldUpdateStashes > 0)
            {
                if (now > oldUpdateStashes)
                    _repo.RefreshStashes();
                else
                    Interlocked.CompareExchange(ref _updateStashes, oldUpdateStashes, -1);
            }

            var oldUpdateTags = Interlocked.Exchange(ref _updateTags, -1);
            if (oldUpdateTags > 0)
            {
                if (now > oldUpdateTags)
                {
                    refreshCommits = true;
                    _repo.RefreshTags();
                }
                else
                {
                    Interlocked.CompareExchange(ref _updateTags, oldUpdateTags, -1);
                }
            }

            if (refreshCommits)
                _repo.RefreshCommits();
        }

        private void PollWSLRepository(object state)
        {
            if (Interlocked.Read(ref _lockCount) > 0 || _pollTimer == null)
                return;

            if (Interlocked.CompareExchange(ref _pollBusy, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var (status, refs) = await Native.WSL.PollRepositoryStateAsync(_root).ConfigureAwait(false);

                    if (status == null && refs == null)
                        return;

                    var firstRound = _lastPolledStatus == null && _lastPolledRefs == null;
                    var statusChanged = !firstRound && status != _lastPolledStatus;
                    var refsChanged = !firstRound && refs != _lastPolledRefs;

                    if (refsChanged)
                        MarkRefsChanged(refs);

                    if (statusChanged)
                        Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(.5).ToFileTime());

                    _lastPolledStatus = status;
                    _lastPolledRefs = refs;
                }
                catch
                {
                    // Ignore polling failures, e.g. the distro is being shut down.
                }
                finally
                {
                    Interlocked.Exchange(ref _pollBusy, 0);
                }
            });
        }

        private void MarkRefsChanged(string refs)
        {
            var desired = DateTime.Now.AddSeconds(.5).ToFileTime();
            var previous = _lastPolledRefs ?? string.Empty;
            var oldLines = new HashSet<string>(previous.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var newLines = refs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            var refreshBranches = false;
            var refreshTags = false;
            var refreshStashes = false;
            foreach (var changed in DiffRefLines(oldLines, newLines))
            {
                if (changed.StartsWith("refs/tags/", StringComparison.Ordinal))
                    refreshTags = true;
                else if (changed.StartsWith("refs/stash", StringComparison.Ordinal))
                    refreshStashes = true;
                else
                    refreshBranches = true;
            }

            if (refreshBranches)
                Interlocked.Exchange(ref _updateBranch, desired);
            if (refreshTags)
                Interlocked.Exchange(ref _updateTags, desired);
            if (refreshStashes)
                Interlocked.Exchange(ref _updateStashes, desired);
        }

        private static IEnumerable<string> DiffRefLines(HashSet<string> oldLines, string[] newLines)
        {
            foreach (var line in newLines)
            {
                if (!oldLines.Contains(line))
                    yield return line;
            }

            var newSet = new HashSet<string>(newLines);
            foreach (var line in oldLines)
            {
                if (!newSet.Contains(line))
                    yield return line;
            }
        }

        private void StartWSLEventWatcher()
        {
            if (_disposed)
                return;

            var proc = Native.WSL.StartFileEventWatcher(_root, HandleWSLFileEvent, OnWSLEventWatcherExited);
            if (proc == null)
            {
                ScheduleWSLWatcherRestart();
                return;
            }

            Interlocked.Exchange(ref _wslWatcherStartedAt, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _eventWatcher, proc);
        }

        private void OnWSLEventWatcherExited(int? exitCode)
        {
            if (_disposed)
                return;

            var proc = Interlocked.Exchange(ref _eventWatcher, null);
            if (proc == null)
                return;

            try
            {
                if (proc is { HasExited: false })
                    proc.Kill();
                proc?.Dispose();
            }
            catch
            {
                // Ignore.
            }

            if (exitCode == 3)
            {
                // Neither `inotifywait` nor `python3` is available in this distro —
                // a deterministic failure, not a crash: no restart loop, no refresh,
                // settle into polling directly.
                StartWSLPolling();
                return;
            }

            // Events may have been missed while the helper was dead; request a full
            // refresh so the UI catches up regardless of what happens next.
            var refreshAt = DateTime.Now.AddSeconds(.5).ToFileTime();
            Interlocked.Exchange(ref _updateBranch, refreshAt);
            Interlocked.Exchange(ref _updateWC, refreshAt);
            Interlocked.Exchange(ref _updateTags, refreshAt);
            Interlocked.Exchange(ref _updateStashes, refreshAt);

            // A helper that survived for a while was healthy, so start counting
            // failures from scratch again. One that died instantly is a symptom
            // (e.g. the distro is going away), so back off further on every quick death.
            var startedAt = Interlocked.Read(ref _wslWatcherStartedAt);
            if (startedAt > 0 && DateTime.UtcNow.Ticks - startedAt >= WSL_WATCHER_STABLE_TICKS)
                Interlocked.Exchange(ref _wslRestartAttempts, 0);

            ScheduleWSLWatcherRestart();
        }

        private void ScheduleWSLWatcherRestart()
        {
            if (_disposed)
                return;

            if (Interlocked.Increment(ref _wslRestartAttempts) > WSL_RESTART_MAX_ATTEMPTS)
            {
                StartWSLPolling();
                return;
            }

            var delay = Math.Min(WSL_RESTART_BASE_DELAY_MS << (_wslRestartAttempts - 1), WSL_RESTART_MAX_DELAY_MS);
            _wslRestartTimer?.Dispose();
            _wslRestartTimer = new Timer(_ => StartWSLEventWatcher(), null, delay, Timeout.Infinite);
        }

        private void StartWSLPolling()
        {
            if (_disposed || Interlocked.CompareExchange(ref _pollStarted, 1, 0) != 0)
                return;

            var period = _windowFocused ? WSL_POLL_INTERVAL_ACTIVE_MS : WSL_POLL_INTERVAL_INACTIVE_MS;
            _pollTimer = new Timer(PollWSLRepository, null, WSL_POLL_INITIAL_DELAY_MS, period);
        }

        private void HandleWSLFileEvent(string path)
        {
            if (_disposed)
                return;

            // The inotify helper reports paths relative to the repository root, in the
            // same shape `inotifywait --format '%w%f'` produces (e.g. `./.git/HEAD`).
            var name = path.Replace('\\', '/').Trim();
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name.Substring(2);

            if (string.IsNullOrEmpty(name) ||
                name.Equals(".git", StringComparison.Ordinal) ||
                name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            if (name.StartsWith(".git/", StringComparison.Ordinal))
            {
                HandleGitDirFileChanged(name.Substring(5));
            }
            else
            {
                var fullpath = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
                HandleWorkingCopyFileChanged(name, fullpath);
            }
        }

        private void OnRepositoryChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name) || e.Name.Equals(".git", StringComparison.Ordinal))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            if (name.StartsWith(".git/", StringComparison.Ordinal))
                HandleGitDirFileChanged(name.Substring(5));
            else
                HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        private void OnGitDirChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            HandleGitDirFileChanged(name);
        }

        private void OnWorkingCopyChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.Equals(".git", StringComparison.Ordinal) ||
                name.StartsWith(".git/", StringComparison.Ordinal) ||
                name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        private void HandleGitDirFileChanged(string name)
        {
            if (name.Contains("fsmonitor--daemon/", StringComparison.Ordinal) ||
                name.EndsWith(".lock", StringComparison.Ordinal) ||
                name.StartsWith("lfs/", StringComparison.Ordinal))
                return;

            if (name.StartsWith("modules", StringComparison.Ordinal))
            {
                if (name.EndsWith("/HEAD", StringComparison.Ordinal) ||
                    name.EndsWith("/ORIG_HEAD", StringComparison.Ordinal))
                {
                    var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                    Interlocked.Exchange(ref _updateSubmodules, desired);
                    Interlocked.Exchange(ref _updateWC, desired);
                }
            }
            else if (name.Equals("MERGE_HEAD", StringComparison.Ordinal) ||
                name.Equals("AUTO_MERGE", StringComparison.Ordinal))
            {
                if (_repo.MayHaveSubmodules())
                    Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
            }
            else if (name.StartsWith("refs/tags", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateTags, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.StartsWith("refs/stash", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateStashes, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.Equals("HEAD", StringComparison.Ordinal) ||
                name.Equals("BISECT_START", StringComparison.Ordinal) ||
                name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                name.StartsWith("refs/remotes/", StringComparison.Ordinal) ||
                (name.StartsWith("worktrees/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                Interlocked.Exchange(ref _updateBranch, DateTime.Now.AddSeconds(.5).ToFileTime());
            }
            else if (name.StartsWith("reftable/", StringComparison.Ordinal))
            {
                var desired = DateTime.Now.AddSeconds(.5).ToFileTime();
                Interlocked.Exchange(ref _updateBranch, desired);
                Interlocked.Exchange(ref _updateTags, desired);
                Interlocked.Exchange(ref _updateStashes, desired);
            }
            else if (name.StartsWith("objects/", StringComparison.Ordinal) || name.Equals("index", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
            }
        }

        private void HandleWorkingCopyFileChanged(string name, string fullpath)
        {
            if (name.StartsWith(".vs/", StringComparison.Ordinal))
                return;

            if (name.Equals(".gitmodules", StringComparison.Ordinal))
            {
                var desired = DateTime.Now.AddSeconds(1).ToFileTime();
                Interlocked.Exchange(ref _updateSubmodules, desired);
                Interlocked.Exchange(ref _updateWC, desired);
                return;
            }

            var dir = Directory.Exists(fullpath) ? fullpath : Path.GetDirectoryName(fullpath);
            if (IsInSubmodule(dir))
            {
                Interlocked.Exchange(ref _updateSubmodules, DateTime.Now.AddSeconds(1).ToFileTime());
                return;
            }

            Interlocked.Exchange(ref _updateWC, DateTime.Now.AddSeconds(1).ToFileTime());
        }

        private bool IsInSubmodule(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Equals(_root, StringComparison.Ordinal))
                return false;

            if (File.Exists($"{folder}/.git"))
                return true;

            return IsInSubmodule(Path.GetDirectoryName(folder));
        }

        private readonly IRepository _repo;
        private readonly string _root;
        private List<FileSystemWatcher> _watchers;
        private Timer _timer;
        private Timer _pollTimer;
        private Timer _wslRestartTimer;
        private Process _eventWatcher;
        private int _pollBusy;
        private int _pollStarted;
        private int _wslRestartAttempts;
        private long _wslWatcherStartedAt;
        private volatile bool _windowFocused = true;
        private volatile bool _disposed;
        private string _lastPolledStatus;
        private string _lastPolledRefs;

        private long _lockCount;
        private long _updateWC;
        private long _updateBranch;
        private long _updateSubmodules;
        private long _updateStashes;
        private long _updateTags;
    }
}
