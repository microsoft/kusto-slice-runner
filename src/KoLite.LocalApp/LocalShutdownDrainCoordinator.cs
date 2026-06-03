using System.Net;

public sealed record LocalShutdownDrainSnapshot(
    string Mode,
    bool IsDrainRequested,
    int ActiveWorkerCount,
    DateTimeOffset? RequestedAtUtc,
    string? Reason,
    DateTimeOffset? LastActiveWorkerTransitionUtc);

public sealed class LocalShutdownDrainCoordinator
{
    private readonly object gate = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int activeWorkerCount;
    private int stopTaskStarted;
    private string mode = "Running";
    private DateTimeOffset? requestedAtUtc;
    private string? reason;
    private DateTimeOffset? lastActiveWorkerTransitionUtc;
    private bool workerDispatcherDrained;

    public bool IsDrainRequested
    {
        get
        {
            lock (gate)
            {
                return !StringComparer.Ordinal.Equals(mode, "Running");
            }
        }
    }

    public bool IsStoppingAfterDrain
    {
        get
        {
            lock (gate)
            {
                return StringComparer.Ordinal.Equals(mode, "Stopping");
            }
        }
    }

    public LocalShutdownDrainSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return SnapshotLocked();
        }
    }

    public LocalShutdownDrainSnapshot RequestDrain(DateTimeOffset requestedAtUtc, string? drainReason)
    {
        lock (gate)
        {
            if (StringComparer.Ordinal.Equals(mode, "Running"))
            {
                mode = "DrainRequested";
                this.requestedAtUtc = requestedAtUtc.ToUniversalTime();
                reason = string.IsNullOrWhiteSpace(drainReason) ? null : drainReason.Trim();
                TryMarkDrainedLocked(requestedAtUtc);
            }

            return SnapshotLocked();
        }
    }

    public void RecordWorkerStarted(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            activeWorkerCount++;
            workerDispatcherDrained = false;
            lastActiveWorkerTransitionUtc = nowUtc.ToUniversalTime();
        }
    }

    public void RecordWorkerCompleted(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            activeWorkerCount = Math.Max(0, activeWorkerCount - 1);
            lastActiveWorkerTransitionUtc = nowUtc.ToUniversalTime();
        }
    }

    public LocalShutdownDrainSnapshot NotifyWorkerDispatcherDrained(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            workerDispatcherDrained = true;
            lastActiveWorkerTransitionUtc = nowUtc.ToUniversalTime();
            TryMarkDrainedLocked(nowUtc);
            return SnapshotLocked();
        }
    }

    public bool TryStartStopWhenDrained() => Interlocked.CompareExchange(ref stopTaskStarted, 1, 0) == 0;

    public async Task WaitForDrainedAsync(CancellationToken cancellationToken = default)
    {
        await drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public LocalShutdownDrainSnapshot MarkStopping(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (!StringComparer.Ordinal.Equals(mode, "Stopping"))
            {
                mode = "Stopping";
                lastActiveWorkerTransitionUtc = nowUtc.ToUniversalTime();
            }

            return SnapshotLocked();
        }
    }

    public static bool IsAllowedDrainRemote(IPAddress? remoteIpAddress) =>
        remoteIpAddress is null || IPAddress.IsLoopback(remoteIpAddress);

    private void TryMarkDrainedLocked(DateTimeOffset nowUtc)
    {
        if (StringComparer.Ordinal.Equals(mode, "DrainRequested") && workerDispatcherDrained && activeWorkerCount == 0)
        {
            mode = "Drained";
            lastActiveWorkerTransitionUtc = nowUtc.ToUniversalTime();
            drained.TrySetResult();
        }
    }

    private LocalShutdownDrainSnapshot SnapshotLocked() =>
        new(
            mode,
            !StringComparer.Ordinal.Equals(mode, "Running"),
            activeWorkerCount,
            requestedAtUtc,
            reason,
            lastActiveWorkerTransitionUtc);
}
