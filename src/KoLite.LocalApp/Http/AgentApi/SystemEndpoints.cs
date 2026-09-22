// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.LocalApp.Application.System;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KoLite.LocalApp.Http.AgentApi
{
    public static class SystemEndpoints
    {
        public static void Map(RouteGroupBuilder api)
        {
            api.MapGet("/system/status", GetStatus)
                .WithTags("System")
                .WithName("GetSystemStatus")
                .WithSummary("Gets detailed local system status and supported API versions.");
        }

        private static Ok<SystemStatusResponse> GetStatus(SystemStatusApplicationService status)
        {
            var model = status.Get();
            var worker = model.WorkerPool;
            var update = model.UpdateSnapshot;
            var retention = model.RetentionSnapshot;
            var shutdown = model.Shutdown;
            return TypedResults.Ok(new SystemStatusResponse(
                "healthy",
                ["v1"],
                new DatabaseStatusResponse("ok", model.DatabasePath, model.JobCount),
                new KustoStatusResponse("enabled", model.KustoAuthMode),
                new SchedulerStatusResponse(
                    model.SchedulerOptions.Enabled,
                    model.SchedulerOptions.TickInterval.ToString(),
                    model.WorkerPoolOptions.MaxDispatchStartsPerCycle,
                    model.WorkerPoolOptions.MaxConcurrencyDisplay,
                    model.SchedulerOptions.LogEveryPass),
                new WorkerPoolStatusResponse(
                    worker.Mode,
                    worker.Enabled,
                    worker.EnabledSource,
                    worker.MaxConcurrency,
                    worker.MaxConcurrencySource,
                    worker.MaxConcurrencyDisplay,
                    worker.IdleDelay,
                    worker.IdleDelaySource,
                    worker.MaxDispatchStartsPerCycle,
                    worker.MaxDispatchStartsPerCycleSource,
                    worker.ActiveWorkerCount,
                    worker.AvailableSlots,
                    worker.ClaimableBacklog,
                    worker.ActiveQueueRows,
                    worker.QueuedQueueRows,
                    worker.LeasedQueueRows,
                    worker.ExpiredLeaseRows,
                    worker.IsIdle,
                    worker.IsSaturated,
                    worker.DispatchCycles,
                    worker.IdleCycles,
                    worker.SaturatedCycles,
                    worker.Starts,
                    worker.Succeeded,
                    worker.RetryableFailures,
                    worker.DeadLettered,
                    worker.Faulted,
                    worker.ActiveWorkerIds,
                    worker.LastUpdatedAtUtc),
                new UpdateStatusResponse(
                    update.Status.ToString(),
                    update.Reason.ToString(),
                    model.UpdateOptions.Enabled,
                    model.UpdateOptions.Repository,
                    "latest-release",
                    model.UpdateOptions.Interval.ToString(),
                    update.BuiltSha,
                    update.RemoteSha,
                    update.LatestVersion,
                    update.ReleaseUrl,
                    update.CommitsBehind,
                    update.CommitsAhead,
                    update.LastCheckedUtc,
                    update.ErrorMessage),
                new RetentionStatusResponse(
                    model.RetentionOptions.Enabled,
                    model.RetentionOptions.Window.TotalDays,
                    model.RetentionOptions.Interval.ToString(),
                    retention.LastRunUtc,
                    retention.TotalDeleted,
                    retention.LogsDeleted,
                    retention.AttemptsDeleted,
                    retention.ScheduledSlicesDeleted,
                    retention.QueueRowsDeleted,
                    retention.LastError),
                new ShutdownStatusResponse(
                    shutdown.Mode,
                    shutdown.IsDrainRequested,
                    shutdown.ActiveWorkerCount,
                    shutdown.RequestedAtUtc,
                    shutdown.Reason,
                    shutdown.LastActiveWorkerTransitionUtc)));
        }
    }
}
