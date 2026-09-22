// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Ksr.Local.Core.Time;

namespace Ksr.LocalApp.FailureAnalysis
{
    public enum FailureAnalysisStatus { Running, Completed, Failed }

    public sealed record FailureAnalysisRun(
        string RunId,
        string JobId,
        FailureAnalysisStatus Status,
        string? Markdown,
        string? Error,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    public sealed class FailureAnalysisRunRegistry
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        private const int MaxEntries = 200;

        private readonly IClock clock;
        private readonly ConcurrentDictionary<string, FailureAnalysisRun> runs = new();
        private readonly object syncLock = new();

        public FailureAnalysisRunRegistry(IClock clock)
        {
            this.clock = clock;
        }

        public bool TryStart(string jobId, out FailureAnalysisRun run)
        {
            lock (syncLock)
            {
                PurgeExpired();

                var now = clock.UtcNow;

                // Return the existing in-flight run if one is already Running for this job.
                foreach (var kvp in runs)
                {
                    if (string.Equals(kvp.Value.JobId, jobId, StringComparison.Ordinal) &&
                        kvp.Value.Status == FailureAnalysisStatus.Running &&
                        !IsExpired(kvp.Value, now))
                    {
                        run = kvp.Value;
                        return false;
                    }
                }

                // Evict oldest non-Running entries when at the cap to make room.
                if (runs.Count >= MaxEntries)
                {
                    EvictOldestNonRunning();
                }

                var newRun = new FailureAnalysisRun(
                    RunId: Guid.NewGuid().ToString("N"),
                    JobId: jobId,
                    Status: FailureAnalysisStatus.Running,
                    Markdown: null,
                    Error: null,
                    StartedAtUtc: now,
                    UpdatedAtUtc: now);

                runs[newRun.RunId] = newRun;
                run = newRun;
                return true;
            }
        }

        public FailureAnalysisRun? Complete(string runId, string markdown)
        {
            lock (syncLock)
            {
                if (!runs.TryGetValue(runId, out var existing)) return null;
                var updated = existing with
                {
                    Status = FailureAnalysisStatus.Completed,
                    Markdown = markdown,
                    UpdatedAtUtc = clock.UtcNow
                };
                runs[runId] = updated;
                return updated;
            }
        }

        public FailureAnalysisRun? Fail(string runId, string error)
        {
            lock (syncLock)
            {
                if (!runs.TryGetValue(runId, out var existing)) return null;
                var updated = existing with
                {
                    Status = FailureAnalysisStatus.Failed,
                    Error = error,
                    UpdatedAtUtc = clock.UtcNow
                };
                runs[runId] = updated;
                return updated;
            }
        }

        // Does not return expired runs — treat expired as absent.
        public FailureAnalysisRun? Get(string runId)
        {
            if (!runs.TryGetValue(runId, out var run)) return null;
            if (IsExpired(run, clock.UtcNow)) return null;
            return run;
        }

        // Returns the run only if it exists, is not expired, and its JobId matches the given jobId.
        public FailureAnalysisRun? GetForJob(string jobId, string runId)
        {
            var run = Get(runId);
            if (run is null) return null;
            if (!string.Equals(run.JobId, jobId, StringComparison.Ordinal)) return null;
            return run;
        }

        private void PurgeExpired()
        {
            var now = clock.UtcNow;
            foreach (var kvp in runs.ToArray())
            {
                if (IsExpired(kvp.Value, now))
                {
                    runs.TryRemove(kvp.Key, out _);
                }
            }
        }

        private void EvictOldestNonRunning()
        {
            var toEvict = runs.Count - MaxEntries + 1;
            if (toEvict <= 0) return;

            var candidates = runs.Values
                .Where(r => r.Status != FailureAnalysisStatus.Running)
                .OrderBy(r => r.UpdatedAtUtc)
                .Take(toEvict)
                .ToList();

            foreach (var candidate in candidates)
            {
                runs.TryRemove(candidate.RunId, out _);
            }
        }

        private static bool IsExpired(FailureAnalysisRun run, DateTimeOffset now)
        {
            return now - run.UpdatedAtUtc > Ttl;
        }
    }
}
