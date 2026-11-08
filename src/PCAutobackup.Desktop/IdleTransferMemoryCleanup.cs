using System.Runtime;

namespace PCAutobackup.Desktop;

/// <summary>One collection after a transfer, once its async stacks have unwound and the UI is idle.</summary>
internal sealed class IdleTransferMemoryCleanup(Action? reclaim = null)
{
    private readonly object stateLock = new();
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private readonly Action reclaim = reclaim ?? ReclaimManagedMemory;
    private long generation;
    private long collectedGeneration = -1;
    private bool jobActive;

    public async Task BeginJobAsync()
    {
        lock (stateLock) { generation++; jobActive = true; }
        // A collection already in progress finishes before new work starts; the UI awaits asynchronously.
        await cleanupGate.WaitAsync().ConfigureAwait(false);
        cleanupGate.Release();
    }

    public long CompleteJob()
    {
        lock (stateLock) { jobActive = false; return generation; }
    }

    public Task ReclaimWhenIdleAsync(long completedGeneration) => Task.Run(async () =>
    {
        await cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (stateLock)
            {
                if (jobActive || generation != completedGeneration || collectedGeneration == completedGeneration) return;
                collectedGeneration = completedGeneration;
            }
            reclaim();
        }
        finally { cleanupGate.Release(); }
    });

    private static void ReclaimManagedMemory()
    {
        // This releases dead managed buffers and requests decommit of unused heap memory.
        // It does not trim/page out the working set, and it does not run on a recurring timer.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
