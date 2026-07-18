using CortexPlexus.Parsing;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Agent;

/// <summary>
/// Watches a project directory for source file changes using OS kernel events (ReadDirectoryChangesW on Windows).
/// Debounces changes to avoid rapid-fire re-indexing.
/// CPU/RAM usage when idle: ~0%.
/// </summary>
public sealed class ProjectFileWatcher : IDisposable
{
    private static readonly string[] WatchedExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".md"];

    private static readonly string[] ExcludedDirs =
        ["bin", "obj", "node_modules", "__pycache__", ".venv", ".git", ".vs", ".idea", "dist", "build", "out"];

    /// <summary>
    /// Consecutive flush failures after which the watcher gives up and fails the watch
    /// session. A supervisor (systemd Restart=) then recycles the whole process — the
    /// alternative is the zombie-watch failure mode: a process that stays "active
    /// (running)" for weeks while every flush dies on the same exception and the
    /// server-side index silently freezes.
    /// </summary>
    internal const int MaxConsecutiveFlushFailures = 5;

    private readonly FileSystemWatcher _watcher;
    private readonly ILogger _logger;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeSpan _debounceDelay;
    private CancellationTokenSource? _debounceCts;
    private readonly string _rootPath;
    private readonly IReadOnlyList<string> _ignorePatterns;
    private int _consecutiveFlushFailures;
    private readonly TaskCompletionSource<Exception> _fatal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Current consecutive flush-failure count (0 = healthy). Reported in heartbeats.</summary>
    public int ConsecutiveFlushFailures => Volatile.Read(ref _consecutiveFlushFailures);

    /// <summary>
    /// Raised after every flush attempt with the outcome: null on success, the exception
    /// on failure. Used by the watch host to push a heartbeat per flush.
    /// </summary>
    public event Action<Exception?>? FlushCompleted;

    public ProjectFileWatcher(string path, ILogger logger, TimeSpan? debounceDelay = null)
    {
        _logger = logger;
        _debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(3);
        _rootPath = Path.GetFullPath(path);
        _ignorePatterns = IgnorePatternMatcher.LoadFromDirectory(_rootPath);
        if (_ignorePatterns.Count > 0)
        {
            _logger.LogInformation(
                "Watch mode honors .cortexplexusignore: {Count} user pattern(s) loaded from {Root}",
                _ignorePatterns.Count, _rootPath);
        }
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Deleted += OnFileChanged;
        _watcher.Error += OnError;
    }

    public async Task WatchAsync(Func<IReadOnlyList<string>, Task> onBatchChanged, CancellationToken ct)
    {
        _watcher.EnableRaisingEvents = true;

        // Store callback for debounce handler
        _onBatchChanged = onBatchChanged;

        try
        {
            // Keep running until cancelled — or until repeated flush failures make
            // continuing pointless (see MaxConsecutiveFlushFailures).
            var stopped = Task.Delay(Timeout.Infinite, ct);
            var finished = await Task.WhenAny(stopped, _fatal.Task);
            if (finished == _fatal.Task)
            {
                var cause = await _fatal.Task;
                throw new WatchFailedException(
                    $"Watch aborted after {MaxConsecutiveFlushFailures} consecutive flush failures. " +
                    "Exiting so a supervisor (systemd Restart=) can recycle the process.", cause);
            }
            await stopped; // propagate OperationCanceledException for graceful stop
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File watcher stopped.");
        }
        finally
        {
            _watcher.EnableRaisingEvents = false;
        }
    }

    private Func<IReadOnlyList<string>, Task>? _onBatchChanged;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShouldWatch(e.FullPath)) return;
        QueueChange(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldWatch(e.FullPath)) QueueChange(e.FullPath);
        if (ShouldWatch(e.OldFullPath)) QueueChange(e.OldFullPath);
    }

    // Instance filter: hardcoded defaults + user's .cortexplexusignore.
    // Static IsWatchedFile retained for unit tests that cover the default list.
    private bool ShouldWatch(string filePath)
    {
        if (!IsWatchedFile(filePath)) return false;
        if (_ignorePatterns.Count > 0 && IgnorePatternMatcher.Matches(filePath, _rootPath, _ignorePatterns))
            return false;
        return true;
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Typical trigger: OS kernel event buffer overflow (~64 KB default).
        // Happens during git checkout / renames / mass refactor — any of the
        // rapid-event burst the ~64 KB buffer cannot absorb. Without recovery
        // we silently miss every change while the buffer was saturated, so
        // the project quietly drifts out of sync.
        //
        // Recovery: enumerate the watched tree ourselves and queue every
        // eligible file. Debounce coalesces them into a single batch; the
        // indexer's SHA256 diff skips unchanged files, so the server only
        // re-ingests what actually moved.
        _logger.LogWarning(e.GetException(),
            "FileSystemWatcher error (likely buffer overflow) — triggering full rescan of {Root}",
            _rootPath);

        try
        {
            foreach (var file in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
            {
                if (ShouldWatch(file)) QueueChange(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full-rescan recovery failed — watch is now degraded; restart the agent to reset");
        }
    }

    private void QueueChange(string filePath)
    {
        lock (_lock)
        {
            _pendingChanges.Add(filePath);
        }
        // Reset debounce timer
        ScheduleFlush(_debounceDelay);
    }

    private void ScheduleFlush(TimeSpan delay)
    {
        lock (_lock)
        {
            try
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Watcher already disposed (shutdown) — a late retry landing here is inert.
            }
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                    await FlushChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    // Debounce reset — ignore
                }
            });
        }
    }

    // Internal so tests can drive flush outcomes without waiting on real FS events.
    internal async Task FlushChangesAsync()
    {
        IReadOnlyList<string> changes;
        lock (_lock)
        {
            if (_pendingChanges.Count == 0) return;
            changes = _pendingChanges.ToList();
            _pendingChanges.Clear();
        }

        if (_onBatchChanged is null) return;

        try
        {
            await _onBatchChanged(changes);
            Volatile.Write(ref _consecutiveFlushFailures, 0);
            FlushCompleted?.Invoke(null);
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFlushFailures);

            // Re-queue the failed batch. The old code dropped it here — combined with
            // the swallow-and-continue below, that produced the zombie watch: weeks of
            // detected-but-never-uploaded changes with no error signal anywhere.
            lock (_lock)
            {
                foreach (var change in changes)
                    _pendingChanges.Add(change);
            }

            _logger.LogError(ex,
                "Error processing {Count} file changes (consecutive failure {Failures}/{Max}) — batch re-queued",
                changes.Count, failures, MaxConsecutiveFlushFailures);
            FlushCompleted?.Invoke(ex);

            if (failures >= MaxConsecutiveFlushFailures)
            {
                _logger.LogCritical(
                    "Watch flush failed {Max} times in a row — giving up so the supervisor can restart the agent.",
                    MaxConsecutiveFlushFailures);
                _fatal.TrySetResult(ex);
                return;
            }

            // Retry with backoff: 30s, 60s, 120s, 240s. A new FS event resets the
            // schedule to the normal 3s debounce, which merges into the same batch.
            var backoff = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, failures - 1), 300));
            _logger.LogInformation("Retrying flush in {Delay:F0}s...", backoff.TotalSeconds);
            ScheduleFlush(backoff);
        }
    }

    // Test hooks — drive the flush pipeline without real FileSystemWatcher events.
    internal void EnqueueChangeForTest(string filePath)
    {
        lock (_lock) { _pendingChanges.Add(filePath); }
    }

    internal int PendingChangeCountForTest
    {
        get { lock (_lock) { return _pendingChanges.Count; } }
    }

    internal void SetCallbackForTest(Func<IReadOnlyList<string>, Task> onBatchChanged)
        => _onBatchChanged = onBatchChanged;

    internal Task<Exception> FatalTaskForTest => _fatal.Task;

    internal static bool IsWatchedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!WatchedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return false;

        // Exclude build/dependency directories
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(p => ExcludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
    }
}

/// <summary>
/// Thrown by <see cref="ProjectFileWatcher.WatchAsync"/> when repeated flush failures
/// make continuing pointless. The watch host converts this into a non-zero exit code
/// so a supervisor (systemd Restart=) recycles the process instead of leaving a
/// zombie watch running.
/// </summary>
public sealed class WatchFailedException(string message, Exception inner) : Exception(message, inner);
