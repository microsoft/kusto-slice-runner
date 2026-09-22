// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Schedules;

namespace Ksr.Local.Core.Scheduling
{
    // The recent-trend health of a job, independent of lifecycle states (paused, completed,
    // soft-deleted) which the dashboard layers on top. Attention means "broken now" and always
    // warrants action; Borderline means "some recent trouble, watch it"; Healthy means the recent
    // window is clean.
    public enum JobHealthTier
    {
        Healthy = 0,
        Borderline = 1,
        Attention = 2
    }

    // Two-dimensional job health used by the dashboard split pill: a recent-trend Tier (the pill
    // color) and, for Complete-policy jobs, a historical-completeness gap count (the second
    // segment). Recent-window failures feed the Tier; the gap count is the lifetime total of
    // unaddressed terminal dead-letters. The two overlap only for failures that are still inside
    // the recent window.
    public sealed record JobHealth(
        JobHealthTier Tier,
        int RecentConsidered,
        int RecentSucceeded,
        int RecentFailed,
        bool TracksCompleteness,
        int GapCount)
    {
        // A Complete-policy job with at least one unaddressed terminal gap. Recent-policy jobs
        // never report gaps regardless of dead-letter history.
        public bool HasGaps => TracksCompleteness && GapCount > 0;
    }

    // Scores a job's dashboard health from the outcomes of its most recent slice windows plus its
    // per-job completeness policy. Pure and deterministic so the thresholds can be unit-tested and
    // tuned in one place; callers (the SQLite read model + dashboard query) supply the recent
    // states and the lifetime dead-letter count.
    public static class JobHealthEvaluator
    {
        // Number of most-recent slice windows (that have a persisted state) considered for the
        // recent-trend Tier. The SQLite read model uses this to bound its window query.
        public const int RecentWindowSize = 10;

        // Fraction of recent "considered" slices that must be failures for a job whose latest
        // considered slice failed to be treated as broken-now (Attention).
        public const double AttentionFailureRatio = 0.5;

        // Slice states that represent a resolved outcome for recent-trend scoring. Pending states
        // (Queued/Running/DependencyBlocked) and absent windows are ignored: they are not failures.
        private const string Completed = "Completed";
        private const string Failed = "Failed";
        private const string DeadLettered = "DeadLettered";

        // Evaluates health for one job.
        // recentStatesNewestFirst: current states of the most recent slice windows, newest first,
        //   already bounded to RecentWindowSize by the caller. Null/absent entries are ignored.
        // policy: the job's completeness policy.
        // lifetimeDeadLetteredCount: total terminal dead-letters across all slice windows.
        public static JobHealth Evaluate(
            IReadOnlyList<string?> recentStatesNewestFirst,
            JobHealthPolicy policy,
            int lifetimeDeadLetteredCount)
        {
            ArgumentNullException.ThrowIfNull(recentStatesNewestFirst);

            var succeeded = 0;
            var failed = 0;
            bool? latestConsideredWasFailure = null;

            foreach (var state in recentStatesNewestFirst)
            {
                var outcome = Classify(state);
                if (outcome is null)
                {
                    continue;
                }

                latestConsideredWasFailure ??= outcome.Value;
                if (outcome.Value)
                {
                    failed++;
                }
                else
                {
                    succeeded++;
                }
            }

            var considered = succeeded + failed;
            var tracksCompleteness = policy == JobHealthPolicy.Complete;
            var gapCount = Math.Max(0, lifetimeDeadLetteredCount);

            var tier = DeriveTier(considered, failed, latestConsideredWasFailure);
            return new JobHealth(tier, considered, succeeded, failed, tracksCompleteness, gapCount);
        }

        private static JobHealthTier DeriveTier(int considered, int failed, bool? latestConsideredWasFailure)
        {
            if (considered == 0 || failed == 0)
            {
                return JobHealthTier.Healthy;
            }

            var failureRatio = (double)failed / considered;
            if (latestConsideredWasFailure == true && failureRatio >= AttentionFailureRatio)
            {
                return JobHealthTier.Attention;
            }

            return JobHealthTier.Borderline;
        }

        // Maps a slice state to a resolved outcome: true = failure, false = success, null = pending
        // (ignored). Completed is the only success; Failed (retry-pending) and DeadLettered
        // (terminal) are both failures for recent-trend purposes, so a sustained failing streak
        // reads as broken even before retries are exhausted.
        private static bool? Classify(string? state) => state switch
        {
            Completed => false,
            Failed => true,
            DeadLettered => true,
            _ => null
        };
    }
}
