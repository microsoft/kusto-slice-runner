using System.Text.Json;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Application.Jobs
{
    public enum JobLifecycleState
    {
        Active,
        Paused,
        SoftDeleted
    }

    public sealed record JobApplicationModel(
        JobCatalogRecord Record,
        JobLifecycleState LifecycleState,
        bool HasStarted);

    public sealed record JobImportApplicationResult(
        int Created,
        int Updated,
        IReadOnlyList<JobCatalogImportItemResult> Items)
    {
        public int Total => Created + Updated;
    }

    public sealed class JobApplicationService
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteJobLifecycleService lifecycle;
        private readonly LifecycleReadModel lifecycleReadModel;

        public JobApplicationService(
            SqliteJobCatalogRepository catalog,
            SqliteJobLifecycleService lifecycle,
            LifecycleReadModel lifecycleReadModel)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
            this.lifecycleReadModel = lifecycleReadModel;
        }

        public IReadOnlyList<JobApplicationModel> List(string? activityId = null)
        {
            var softDeleted = SoftDeletedJobIds();
            return catalog.List()
                .Where(record => activityId is null || StringComparer.Ordinal.Equals(record.ActivityId, activityId))
                .Select(record => Build(record, softDeleted.Contains(record.JobId)))
                .ToArray();
        }

        public JobApplicationModel Get(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                throw NotFound(jobId);
            }

            return Build(record, SoftDeletedJobIds().Contains(record.JobId));
        }

        public JobApplicationModel Create(string scheduleJson, string actor)
        {
            try
            {
                var record = catalog.Create(scheduleJson, actor);
                return Build(record, isSoftDeleted: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                throw InvalidSchedule(ex);
            }
        }

        public JobApplicationModel Replace(string jobId, string scheduleJson, long expectedVersion, string actor)
        {
            if (SoftDeletedJobIds().Contains(jobId))
            {
                throw SoftDeletedJob(jobId, "updated");
            }

            try
            {
                var record = catalog.Update(jobId, scheduleJson, expectedVersion, actor);
                return Build(record, SoftDeletedJobIds().Contains(record.JobId));
            }
            catch (CatalogVersionConflictException ex)
            {
                throw VersionConflict(jobId, ex.ActualVersion);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                if (catalog.Get(jobId) is null)
                {
                    throw NotFound(jobId);
                }

                throw InvalidSchedule(ex);
            }
        }

        public JobApplicationModel Pause(string jobId, long expectedVersion, string actor, string reason)
        {
            return SetPaused(jobId, expectedVersion, isPaused: true, actor, reason);
        }

        public JobApplicationModel Resume(string jobId, long expectedVersion, string actor, string reason)
        {
            return SetPaused(jobId, expectedVersion, isPaused: false, actor, reason);
        }

        public JobApplicationModel SoftDelete(string jobId, long expectedVersion, string actor, string reason, bool force)
        {
            try
            {
                var record = lifecycle.SoftDelete(jobId, expectedVersion, actor, reason, force);
                return Build(record, isSoftDeleted: true);
            }
            catch (DownstreamDependentsException ex)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "active-dependents",
                    "Active dependent jobs block soft delete.",
                    ex.Message,
                    new Dictionary<string, object?>
                    {
                        ["dependents"] = ex.Dependents.Select(item => new { jobId = item.JobId, activityId = item.ActivityId }).ToArray()
                    },
                    ex);
            }
            catch (CatalogVersionConflictException ex)
            {
                throw VersionConflict(jobId, ex.ActualVersion);
            }
            catch (InvalidOperationException ex)
            {
                if (catalog.Get(jobId) is null)
                {
                    throw NotFound(jobId);
                }

                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "invalid-job-state",
                    "The job cannot be soft-deleted.",
                    ex.Message,
                    innerException: ex);
            }
        }

        public JobApplicationModel Restore(string jobId, long expectedVersion, string actor, string reason)
        {
            try
            {
                var record = lifecycle.Restore(jobId, expectedVersion, actor, reason);
                return Build(record, isSoftDeleted: false);
            }
            catch (CatalogVersionConflictException ex)
            {
                throw VersionConflict(jobId, ex.ActualVersion);
            }
            catch (InvalidOperationException ex)
            {
                if (catalog.Get(jobId) is null)
                {
                    throw NotFound(jobId);
                }

                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "invalid-job-state",
                    "The job cannot be restored.",
                    ex.Message,
                    innerException: ex);
            }
        }

        public JobImportApplicationResult Import(string importJson, string actor)
        {
            try
            {
                var parsed = ScheduleImportParser.Parse(importJson);
                if (parsed.IsValid)
                {
                    var softDeleted = SoftDeletedJobIds();
                    foreach (var item in parsed.Items)
                    {
                        var existing = item.Definition.Id is { } id
                            ? catalog.Get(id)
                            : catalog.GetByActivityId(item.Definition.ActivityId);
                        if (existing is not null && softDeleted.Contains(existing.JobId))
                        {
                            throw SoftDeletedJob(existing.JobId, "imported");
                        }
                    }
                }

                var result = catalog.Import(importJson, actor);
                return new JobImportApplicationResult(result.Created, result.Updated, result.Items);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                throw InvalidSchedule(ex);
            }
        }

        public string ExportAll()
        {
            return catalog.ExportAll(SoftDeletedJobIds());
        }

        public string Export(string jobId)
        {
            try
            {
                return catalog.Export(jobId);
            }
            catch (InvalidOperationException)
            {
                throw NotFound(jobId);
            }
        }

        private JobApplicationModel SetPaused(
            string jobId,
            long expectedVersion,
            bool isPaused,
            string actor,
            string reason)
        {
            var current = catalog.Get(jobId);
            if (current is null)
            {
                throw NotFound(jobId);
            }

            if (SoftDeletedJobIds().Contains(jobId))
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "soft-deleted-job",
                    "The job is soft-deleted.",
                    $"Job '{jobId}' must be restored before it can be {(isPaused ? "paused" : "resumed")}.");
            }

            JobCatalogRecord updated;
            try
            {
                updated = catalog.SetEnabled(jobId, enabled: !isPaused, expectedVersion, actor);
            }
            catch (CatalogVersionConflictException ex)
            {
                throw VersionConflict(jobId, ex.ActualVersion);
            }

            lifecycle.RecordTransition(jobId, isPaused ? "Paused" : "Resumed", actor, reason, new { expectedVersion });
            return Build(updated, isSoftDeleted: false);
        }

        private JobApplicationModel Build(JobCatalogRecord record, bool isSoftDeleted)
        {
            var state = isSoftDeleted
                ? JobLifecycleState.SoftDeleted
                : record.IsEnabled
                    ? JobLifecycleState.Active
                    : JobLifecycleState.Paused;
            return new JobApplicationModel(record, state, catalog.HasStarted(record.JobId));
        }

        private HashSet<string> SoftDeletedJobIds()
        {
            return lifecycleReadModel.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static ApplicationProblemException NotFound(string jobId)
        {
            return new ApplicationProblemException(
                StatusCodes.Status404NotFound,
                "job-not-found",
                "Job not found.",
                $"Job '{jobId}' does not exist.");
        }

        private static ApplicationProblemException InvalidSchedule(Exception ex)
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-schedule",
                "The schedule is invalid.",
                ex.Message,
                innerException: ex);
        }

        private static ApplicationProblemException VersionConflict(string jobId, long? actualVersion)
        {
            return new ApplicationProblemException(
                StatusCodes.Status412PreconditionFailed,
                "etag-mismatch",
                "The job changed since it was read.",
                $"The supplied If-Match value does not match the current version of job '{jobId}'.",
                new Dictionary<string, object?>
                {
                    ["currentVersion"] = actualVersion
                });
        }

        private static ApplicationProblemException SoftDeletedJob(string jobId, string operation)
        {
            return new ApplicationProblemException(
                StatusCodes.Status409Conflict,
                "soft-deleted-job",
                "The job is soft-deleted.",
                $"Job '{jobId}' must be restored before it can be {operation}.");
        }
    }
}
