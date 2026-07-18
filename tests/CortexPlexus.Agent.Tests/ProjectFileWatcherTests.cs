using CortexPlexus.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Agent.Tests;

/// <summary>
/// Tests cho ProjectFileWatcher.IsWatchedFile — pure filter logic — plus the
/// flush-failure handling (zombie-watch fix): re-queue on failure, consecutive
/// failure counting, fatal signal after MaxConsecutiveFlushFailures.
///
/// Phạm vi: TEST-PLAN.md #94, #95, #96
///
/// Lưu ý: debounce behavior (#92, #93) cần real FileSystemWatcher → flaky test.
/// Flush-failure tests drive FlushChangesAsync directly via internal hooks —
/// deterministic, no FS events involved.
/// </summary>
public class ProjectFileWatcherTests
{
    // Watcher ctor needs an existing directory for FileSystemWatcher.
    private static (ProjectFileWatcher watcher, Action cleanup) CreateWatcher()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cortex-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var watcher = new ProjectFileWatcher(dir, NullLogger.Instance, debounceDelay: TimeSpan.FromMilliseconds(10));
        return (watcher, () =>
        {
            watcher.Dispose();
            Directory.Delete(dir, recursive: true);
        });
    }

    // === Zombie-watch fix: flush failure handling ===

    [Fact]
    public async Task FlushChangesAsync_CallbackFails_RequeuesBatchAndCountsFailure()
    {
        var (watcher, cleanup) = CreateWatcher();
        try
        {
            watcher.SetCallbackForTest(_ => throw new InvalidOperationException("boom"));
            watcher.EnqueueChangeForTest("/repo/a.cs");
            watcher.EnqueueChangeForTest("/repo/b.cs");

            await watcher.FlushChangesAsync();

            // The failed batch must NOT be dropped — that was the data-loss half of
            // the zombie-watch bug (changes detected but never uploaded, forever).
            Assert.Equal(2, watcher.PendingChangeCountForTest);
            Assert.Equal(1, watcher.ConsecutiveFlushFailures);
            Assert.False(watcher.FatalTaskForTest.IsCompleted);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task FlushChangesAsync_SuccessAfterFailures_ResetsCounter()
    {
        var (watcher, cleanup) = CreateWatcher();
        try
        {
            var shouldFail = true;
            watcher.SetCallbackForTest(_ =>
                shouldFail ? throw new InvalidOperationException("boom") : Task.CompletedTask);

            watcher.EnqueueChangeForTest("/repo/a.cs");
            await watcher.FlushChangesAsync();
            Assert.Equal(1, watcher.ConsecutiveFlushFailures);

            shouldFail = false;
            await watcher.FlushChangesAsync(); // re-queued batch flushes clean
            Assert.Equal(0, watcher.ConsecutiveFlushFailures);
            Assert.Equal(0, watcher.PendingChangeCountForTest);
            Assert.False(watcher.FatalTaskForTest.IsCompleted);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task FlushChangesAsync_MaxConsecutiveFailures_SignalsFatal()
    {
        var (watcher, cleanup) = CreateWatcher();
        try
        {
            var cause = new InvalidOperationException("MSBuildWorkspace exploded");
            watcher.SetCallbackForTest(_ => throw cause);
            watcher.EnqueueChangeForTest("/repo/a.cs");

            for (var i = 0; i < ProjectFileWatcher.MaxConsecutiveFlushFailures; i++)
                await watcher.FlushChangesAsync();

            // 5th consecutive failure → fatal signal so the host exits non-zero and
            // systemd recycles the process instead of leaving a zombie watch.
            Assert.True(watcher.FatalTaskForTest.IsCompleted);
            Assert.Same(cause, await watcher.FatalTaskForTest);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task WatchAsync_FatalSignal_ThrowsWatchFailedException()
    {
        var (watcher, cleanup) = CreateWatcher();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var watchTask = watcher.WatchAsync(_ => throw new InvalidOperationException("boom"), cts.Token);

            watcher.EnqueueChangeForTest("/repo/a.cs");
            for (var i = 0; i < ProjectFileWatcher.MaxConsecutiveFlushFailures; i++)
                await watcher.FlushChangesAsync();

            var ex = await Assert.ThrowsAsync<WatchFailedException>(() => watchTask);
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task FlushChangesAsync_FlushCompleted_ReportsOutcome()
    {
        var (watcher, cleanup) = CreateWatcher();
        try
        {
            var outcomes = new List<Exception?>();
            watcher.FlushCompleted += outcomes.Add;

            watcher.SetCallbackForTest(_ => throw new InvalidOperationException("boom"));
            watcher.EnqueueChangeForTest("/repo/a.cs");
            await watcher.FlushChangesAsync();

            watcher.SetCallbackForTest(_ => Task.CompletedTask);
            await watcher.FlushChangesAsync();

            Assert.Equal(2, outcomes.Count);
            Assert.IsType<InvalidOperationException>(outcomes[0]);
            Assert.Null(outcomes[1]);
        }
        finally
        {
            cleanup();
        }
    }

    // === #95: Filter_WatchedExtensions_Only ===
    [Theory]
    [InlineData("file.cs", true)]
    [InlineData("file.ts", true)]
    [InlineData("file.tsx", true)]
    [InlineData("file.js", true)]
    [InlineData("file.jsx", true)]
    [InlineData("file.py", true)]
    [InlineData("file.md", true)]
    public void IsWatchedFile_WatchedExtensions_ReturnsTrue(string filename, bool expected)
    {
        // Mục đích: Tất cả extension trong WatchedExtensions được chấp nhận.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("file.jpg")]
    [InlineData("file.png")]
    [InlineData("file.dll")]
    [InlineData("file.exe")]
    [InlineData("file.pdb")]
    [InlineData("file.json")]
    [InlineData("file.xml")]
    [InlineData("file")] // no extension
    public void IsWatchedFile_NonWatchedExtensions_ReturnsFalse(string filename)
    {
        // Mục đích: Extension không trong watchlist → bị filter ra.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.False(result);
    }

    // === #96: Filter_CaseInsensitive ===
    [Theory]
    [InlineData("file.CS")]
    [InlineData("file.TS")]
    [InlineData("file.Py")]
    [InlineData("FILE.CS")]
    public void IsWatchedFile_CaseInsensitive_Accepted(string filename)
    {
        // Mục đích: Extension matching là case-insensitive.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.True(result);
    }

    // === #94: Filter_ExcludedDirs_Ignored ===
    [Theory]
    [InlineData("/repo/bin/Debug/App.cs")]
    [InlineData("/repo/obj/project.cs")]
    [InlineData("/repo/node_modules/lib/index.ts")]
    [InlineData("/repo/.git/config.cs")]
    [InlineData("/repo/.venv/site-packages/mod.py")]
    [InlineData("/repo/__pycache__/mod.py")]
    [InlineData("/repo/dist/bundle.js")]
    [InlineData("/repo/.vs/settings.cs")]
    [InlineData("/repo/.idea/config.cs")]
    [InlineData("/repo/out/build.cs")]
    [InlineData("/repo/build/output.cs")]
    public void IsWatchedFile_InExcludedDirectory_ReturnsFalse(string fullPath)
    {
        // Mục đích: Files trong build/dependency dirs bị filter ra dù extension hợp lệ.
        var result = ProjectFileWatcher.IsWatchedFile(fullPath);
        Assert.False(result);
    }

    [Theory]
    [InlineData("/repo/BIN/App.cs")]
    [InlineData("/repo/NODE_MODULES/lib.ts")]
    [InlineData("/repo/Node_Modules/lib.ts")]
    public void IsWatchedFile_ExcludedDirs_CaseInsensitive(string fullPath)
    {
        // Mục đích: Excluded dir matching cũng là case-insensitive.
        var result = ProjectFileWatcher.IsWatchedFile(fullPath);
        Assert.False(result);
    }

    [Fact]
    public void IsWatchedFile_NestedDeep_StillRespectsExclusion()
    {
        // Mục đích: Exclusion dir ở giữa path vẫn filter (không chỉ top-level).
        var path = Path.Combine("/repo", "src", "SubProject", "bin", "Debug", "Service.cs");
        Assert.False(ProjectFileWatcher.IsWatchedFile(path));
    }

    [Fact]
    public void IsWatchedFile_NormalSourceFile_Accepted()
    {
        // Mục đích: Sanity — happy path file thực sự được accept.
        var path = Path.Combine("/repo", "src", "Services", "PaymentService.cs");
        Assert.True(ProjectFileWatcher.IsWatchedFile(path));
    }
}
