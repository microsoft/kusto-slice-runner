using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Pages.Catalog
{
    internal sealed record BulkOperationResult(int Changed, int Skipped, int Conflicted)
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
                message += $" {Conflicted} skipped because they changed since the page loaded.";
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
            foreach (var (jobId, expectedVersion) in Pair(jobIds, expectedVersions))
            {
                var current = catalog.Get(jobId);
                if (current is null || softDeleted.Contains(jobId))
                {
                    skipped++;
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

            return new BulkOperationResult(changed, skipped, conflicted);
        }

        private static HashSet<string> SoftDeletedJobIds(LifecycleReadModel lifecycle) =>
            lifecycle.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);
    }
}
