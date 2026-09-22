// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Ksr.Local.Core.Orchestration;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.State;

namespace Ksr.LocalApp.Application.Jobs
{
    public sealed record JobStatusApplicationModel(
        JobApplicationModel Job,
        JobStatusSummary? SliceStates,
        int Queued,
        int Leased);

    public sealed record CatalogRevisionApplicationModel(
        string EventId,
        long CatalogVersion,
        string EventType,
        string? Actor,
        DateTimeOffset RecordedAtUtc,
        string ScheduleJson);

    public sealed record DependencyReferenceApplicationModel(
        string? JobId,
        string? ActivityId,
        bool Exists);

    public sealed record MissingUpstreamSliceApplicationModel(
        string Reference,
        string? ActivityId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    public sealed record BlockedSliceApplicationModel(
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        bool IsReady,
        IReadOnlyList<MissingUpstreamSliceApplicationModel> Missing);

    public sealed record JobDependenciesApplicationModel(
        JobApplicationModel Job,
        IReadOnlyList<DependencyReferenceApplicationModel> Dependencies,
        int BlockedSliceCount,
        IReadOnlyList<BlockedSliceApplicationModel> BlockedSamples);

    public sealed class JobProjectionApplicationService
    {
        private const int DependencySampleSize = 20;
        private readonly JobApplicationService jobs;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteOperationalReadModelRepository operational;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteSliceStateRepository state;
        private readonly IClock clock;

        public JobProjectionApplicationService(
            JobApplicationService jobs,
            SqliteJobCatalogRepository catalog,
            SqliteOperationalReadModelRepository operational,
            SqliteDiagnosticsReadModelRepository diagnostics,
            SqliteWorkQueueRepository queue,
            SqliteSliceStateRepository state,
            IClock clock)
        {
            this.jobs = jobs;
            this.catalog = catalog;
            this.operational = operational;
            this.diagnostics = diagnostics;
            this.queue = queue;
            this.state = state;
            this.clock = clock;
        }

        public JobStatusApplicationModel GetStatus(string jobId)
        {
            var job = jobs.Get(jobId);
            var summary = operational.GetJobStatusSummaries().FirstOrDefault(item => item.JobId == jobId);
            var queueCounts = queue.CountActiveByState(jobId);
            return new JobStatusApplicationModel(
                job,
                summary,
                queueCounts.Queued,
                queueCounts.Leased);
        }

        public IReadOnlyList<CatalogRevisionApplicationModel> GetCatalogRevisions(string jobId)
        {
            _ = jobs.Get(jobId);
            return catalog.History(jobId)
                .OrderByDescending(item => item.CatalogVersion)
                .ThenByDescending(item => item.RecordedAtUtc)
                .Select(item =>
                {
                    using var payload = JsonDocument.Parse(item.PayloadJson);
                    var actor = payload.RootElement.TryGetProperty("actor", out var actorElement)
                        && actorElement.ValueKind == JsonValueKind.String
                        ? actorElement.GetString()
                        : null;
                    var scheduleJson = payload.RootElement.TryGetProperty("scheduleJson", out var scheduleElement)
                        && scheduleElement.ValueKind == JsonValueKind.String
                        ? scheduleElement.GetString()
                        : null;
                    return new CatalogRevisionApplicationModel(
                        item.EventId,
                        item.CatalogVersion,
                        item.EventType,
                        actor,
                        item.RecordedAtUtc,
                        scheduleJson ?? "{}");
                })
                .ToArray();
        }

        public JobDependenciesApplicationModel GetDependencies(string jobId)
        {
            var job = jobs.Get(jobId);
            var record = job.Record;
            var dependencies = record.Definition.DependsOn.Select(dependency =>
            {
                var upstream = dependency.Id is not null
                    ? catalog.Get(dependency.Id)
                    : dependency.ActivityId is not null
                        ? catalog.GetByActivityId(dependency.ActivityId)
                        : null;
                return new DependencyReferenceApplicationModel(
                    dependency.Id,
                    upstream?.Definition.ActivityId ?? dependency.ActivityId,
                    upstream is not null);
            }).ToArray();

            var blockedCount = operational.GetJobStatusSummaries()
                .FirstOrDefault(item => item.JobId == jobId)?.DependencyBlockedCount ?? 0;
            var jobsById = catalog.List()
                .Where(item => item.Definition.Id is not null)
                .ToDictionary(item => item.Definition.Id!, item => item.Definition, StringComparer.Ordinal);
            var completed = state.ListCompletedSliceKeys();
            var samples = new List<BlockedSliceApplicationModel>();
            foreach (var slice in diagnostics.GetSlices(jobId, "DependencyBlocked", null, null, clock.UtcNow, DependencySampleSize))
            {
                DependencyReadiness readiness;
                try
                {
                    readiness = DependencyReadinessEvaluator.Evaluate(
                        record.Definition,
                        new SliceRange(jobId, slice.SliceStartUtc, slice.SliceEndUtc),
                        jobsById,
                        completed);
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
                {
                    continue;
                }

                var missing = readiness.MissingSlices.Select(key =>
                {
                    if (SliceKey.TryParse(key.Value, out _, out var parsed))
                    {
                        return new MissingUpstreamSliceApplicationModel(
                            parsed.JobId,
                            catalog.Get(parsed.JobId)?.Definition.ActivityId,
                            parsed.StartUtc,
                            parsed.EndUtc);
                    }

                    return new MissingUpstreamSliceApplicationModel(
                        key.Value,
                        null,
                        slice.SliceStartUtc,
                        slice.SliceEndUtc);
                }).ToArray();
                samples.Add(new BlockedSliceApplicationModel(
                    slice.SliceStartUtc,
                    slice.SliceEndUtc,
                    readiness.IsReady,
                    missing));
            }

            return new JobDependenciesApplicationModel(job, dependencies, blockedCount, samples);
        }
    }
}
