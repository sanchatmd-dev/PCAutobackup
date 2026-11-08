using PCAutobackup.Desktop;

namespace PCAutobackup.Tests;

public sealed class IdleTransferMemoryCleanupTests
{
    [Fact]
    public async Task CompletedTransferCollectsOnceOnBackgroundThread()
    {
        var collections = 0;
        var cleanup = new IdleTransferMemoryCleanup(() =>
        {
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            Interlocked.Increment(ref collections);
        });
        await cleanup.BeginJobAsync();
        var generation = cleanup.CompleteJob();

        await Task.WhenAll(cleanup.ReclaimWhenIdleAsync(generation), cleanup.ReclaimWhenIdleAsync(generation));

        Assert.Equal(1, collections);
    }

    [Fact]
    public async Task NewJobInvalidatesPreviouslyQueuedCollection()
    {
        var collections = 0;
        var cleanup = new IdleTransferMemoryCleanup(() => Interlocked.Increment(ref collections));
        await cleanup.BeginJobAsync();
        var previous = cleanup.CompleteJob();
        await cleanup.BeginJobAsync();

        await cleanup.ReclaimWhenIdleAsync(previous);
        Assert.Equal(0, collections);
        var current = cleanup.CompleteJob();
        await cleanup.ReclaimWhenIdleAsync(previous);
        Assert.Equal(0, collections);
        await cleanup.ReclaimWhenIdleAsync(current);
        Assert.Equal(1, collections);
    }

    [Fact]
    public async Task NewWorkAsynchronouslyWaitsForAlreadyRunningCollection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var cleanup = new IdleTransferMemoryCleanup(() =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        await cleanup.BeginJobAsync();
        var collecting = cleanup.ReclaimWhenIdleAsync(cleanup.CompleteJob());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var nextJob = cleanup.BeginJobAsync();
        try { Assert.False(nextJob.IsCompleted); }
        finally { release.Set(); }
        await Task.WhenAll(collecting, nextJob).WaitAsync(TimeSpan.FromSeconds(5));
        cleanup.CompleteJob();
    }

    [Fact]
    public async Task FailedCollectionReleasesGateAndDoesNotRetrySameTransfer()
    {
        var collections = 0;
        var cleanup = new IdleTransferMemoryCleanup(() =>
        {
            if (Interlocked.Increment(ref collections) == 1) throw new InvalidOperationException("Synthetic cleanup failure");
        });
        await cleanup.BeginJobAsync();
        var previous = cleanup.CompleteJob();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ReclaimWhenIdleAsync(previous));
        await cleanup.ReclaimWhenIdleAsync(previous);
        await cleanup.BeginJobAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await cleanup.ReclaimWhenIdleAsync(cleanup.CompleteJob()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, collections);
    }
}
