// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Pages.Catalog
{
    internal sealed record BulkOperationResult(int Changed, int Skipped, int Conflicted, IReadOnlyList<string>? BlockedByDependents = null)
    {
        public string ToMessage(string verb)
        {
            var message = $"{verb} {Changed} job(s).";
            if (Skipped > 0)
            {
                message += $" {Skipped} already in the requested state or no longer eligible.";
            }

            if (Conflicted > 0)
            {
                message += $" {Conflicted} skipped because they changed since the page loaded. Review the refreshed jobs and try again.";
            }

            if (BlockedByDependents is { Count: > 0 } blocked)
            {
                message += $" {blocked.Count} skipped because other active jobs depend on them: {string.Join("; ", blocked)}.";
            }

            return message;
        }
    }

    internal static class CatalogBulkOperations
    {
        public const string TempDataKey = "BulkOperationSummary";

        public static IReadOnlyList<(string JobId, long ExpectedVersion)> Pair(string[]? jobIds, long[]? expectedVersions)
        {
            jobIds ??= Array.Empty<string>();
            expectedVersions ??= Array.Empty<long>();
            if (jobIds.Length != expectedVersions.Length)
            {
                throw new InvalidOperationException("Each selected job must include its catalog version.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<(string JobId, long ExpectedVersion)>();
            for (var index = 0; index < jobIds.Length; index++)
            {
                var jobId = jobIds[index];
                if (string.IsNullOrWhiteSpace(jobId) || !seen.Add(jobId))
                {
                    continue;
                }

                pairs.Add((jobId, expectedVersions[index]));
            }

            return pairs;
        }

        public static BulkOperationResult SetEnabled(
            SqliteJobCatalogRepository catalog,
            LifecycleReadModel lifecycle,
            string[]? jobIds,
            long[]? expectedVersions,
            bool targetEnabled)
        {
            var softDeleted = SoftDeletedJobIds(lifecycle);
            var changed = 0;
            var skipped = 0;
            var conflicted = 0;
            foreach (var (jobId, expectedVersion) in Pair(jobIds, expectedVersions))
            {
                var current = catalog.Get(jobId);
                if (current is null || softDeleted.Contains(jobId) || current.IsEnabled == targetEnabled)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    catalog.SetEnabled(jobId, targetEnabled, expectedVersion, actor: "local-web");
                    changed++;
                }
                catch (InvalidOperationException)
                {
                    conflicted++;
                }
            }

            return new BulkOperationResult(changed, skipped, conflicted);
        }

        public static BulkOperationResult SetAllEnabled(
            SqliteJobCatalogRepository catalog,
            LifecycleReadModel lifecycle,
            bool targetEnabled)
        {
            var softDeleted = SoftDeletedJobIds(lifecycle);
            var candidates = catalog.List()
                .Where(job => !softDeleted.Contains(job.JobId) && job.IsEnabled != targetEnabled)
                .ToArray();
            return SetEnabled(
                catalog,
                lifecycle,
                candidates.Select(job => job.JobId).ToArray(),
                candidates.Select(job => job.CatalogVersion).ToArray(),
                targetEnabled);
        }

        public static BulkOperationResult SoftDelete(
            SqliteJobCatalogRepository catalog,
            SqliteJobLifecycleService lifecycle,
            LifecycleReadModel lifecycleReadModel,
            string[]? jobIds,
            long[]? expectedVersions)
        {
            var softDeleted = SoftDeletedJobIds(lifecycleReadModel);
            var changed = 0;
            var skipped = 0;
            var conflicted = 0;
            var blocked = new List<string>();
            foreach (var (jobId, expectedVersion) in Pair(jobIds, expectedVersions))
            {
                var current = catalog.Get(jobId);
                if (current is null || softDeleted.Contains(jobId))
                {
                    skipped++;
                    continue;
                }

                // Bulk does not auto-force: a job with active downstream dependents is skipped and named
                // (with its dependents) in the summary so the operator knows why it was left behind.
                var dependents = lifecycle.GetActiveDependents(jobId);
                if (dependents.Count > 0)
                {
                    blocked.Add($"{current.ActivityId} (needed by {string.Join(", ", dependents.Select(dependent => dependent.ActivityId))})");
                    continue;
                }

                try
                {
                    lifecycle.SoftDelete(jobId, expectedVersion, actor: "local-web", reason: "Soft deleted from dashboard (bulk)");
                    changed++;
                }
                catch (InvalidOperationException)
                {
                    conflicted++;
                }
            }

            return new BulkOperationResult(changed, skipped, conflicted, blocked);
        }

        private static HashSet<string> SoftDeletedJobIds(LifecycleReadModel lifecycle) =>
            lifecycle.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);
    }
}
