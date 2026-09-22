// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.LocalApp.Application.Jobs;

namespace KoLite.LocalApp.Http.AgentApi
{
    internal static class AgentContractMapper
    {
        public static JobSummaryResponse Job(JobApplicationModel model)
        {
            var record = model.Record;
            var definition = record.Definition;
            return new JobSummaryResponse(
                record.JobId,
                definition.ActivityId,
                Lifecycle(model.LifecycleState),
                model.HasStarted,
                definition.Tags,
                new JobTargetResponse(definition.Target.ClusterUri, definition.Target.Database),
                record.CatalogVersion,
                record.CreatedAtUtc,
                record.UpdatedAtUtc);
        }

        public static JobDetailResponse JobDetail(JobApplicationModel model)
        {
            using var schedule = JsonDocument.Parse(model.Record.ScheduleJson);
            return new JobDetailResponse(Job(model), schedule.RootElement.Clone());
        }

        public static QueueItemResponse Queue(DurableWorkItem item)
        {
            return new QueueItemResponse(
                item.QueueItemId,
                item.JobId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.QueueName,
                item.Priority,
                item.State.ToString(),
                item.AvailableAtUtc,
                item.LockedBy,
                item.LockedUntilUtc,
                item.Attempts,
                item.MaxAttempts,
                item.CreatedAtUtc,
                item.UpdatedAtUtc,
                item.ChunkId,
                item.TotalChunks);
        }

        public static SliceResponse Slice(SliceStateReadout item)
        {
            return new SliceResponse(
                item.JobId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.State,
                item.Attempt,
                item.LeaseOwner,
                item.LeaseExpiresAtUtc,
                item.LeaseExpired,
                item.LastErrorCode,
                item.LastErrorMessage,
                item.UpdatedAtUtc);
        }

        public static AttemptResponse Attempt(SliceAttemptRow item)
        {
            return new AttemptResponse(
                item.AttemptId,
                item.JobId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.Attempt,
                item.Status,
                item.WorkerId,
                item.StartedAtUtc,
                item.CompletedAtUtc,
                item.ErrorCode,
                item.ErrorMessage,
                item.ChunkId,
                item.TotalChunks);
        }

        public static SliceEventResponse Event(SliceStateEventRow item)
        {
            return new SliceEventResponse(
                item.EventId,
                item.JobId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.EventType,
                item.State,
                item.Attempt,
                item.Reason,
                item.Actor,
                item.RecordedAtUtc);
        }

        public static OperationalLogResponse Log(DiagnosticsLogReadout item)
        {
            return new OperationalLogResponse(
                item.LogId,
                item.JobId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.Level,
                item.Message,
                item.Category,
                item.Exception,
                item.RecordedAtUtc,
                item.ChunkId,
                item.TotalChunks);
        }

        public static AuditEventResponse Audit(AuditEventReadout item)
        {
            return new AuditEventResponse(
                item.AuditId,
                item.Actor,
                item.Action,
                item.SubjectType,
                item.SubjectId,
                item.PayloadJson,
                item.RecordedAtUtc);
        }

        public static RerunBatchSummaryResponse Rerun(RerunBatchSummary item)
        {
            return new RerunBatchSummaryResponse(
                item.RerunBatchId,
                item.RootJobId,
                item.RootStartUtc,
                item.RootEndUtc,
                item.RequestedBy,
                item.Reason,
                item.Status,
                item.KustoCleanupAcknowledged,
                item.RequestedAtUtc,
                item.CompletedAtUtc);
        }

        public static RerunBatchDetailResponse RerunDetail(RerunBatchReadout item)
        {
            return new RerunBatchDetailResponse(
                item.RerunBatchId,
                item.RootJobId,
                item.RootStartUtc,
                item.RootEndUtc,
                item.RequestedBy,
                item.Reason,
                item.Status.ToString(),
                item.KustoCleanupAcknowledged,
                item.KustoCleanupCommands,
                item.RequestedAtUtc,
                item.CompletedAtUtc,
                item.Slices.Select(slice => new RerunAffectedSliceResponse(
                    slice.JobId,
                    slice.Slice.StartUtc,
                    slice.Slice.EndUtc,
                    slice.Role.ToString(),
                    slice.OutputTable,
                    slice.ClusterUri,
                    slice.Database,
                    slice.PreviousState,
                    slice.PreviousAttempt,
                    slice.QueueRows,
                    slice.AttemptRows,
                    slice.EventRows,
                    slice.LogRows,
                    slice.ScheduledRows,
                    slice.BlockerReason,
                    slice.Status.ToString())).ToArray());
        }

        public static RepairBatchSummaryResponse Repair(RepairBatchSummary item)
        {
            return new RepairBatchSummaryResponse(
                item.RepairBatchId,
                item.JobId,
                item.RequestedBy,
                item.Reason,
                item.Status,
                item.RequestedAtUtc,
                item.CompletedAtUtc);
        }

        public static RunningSliceResponse RunningSlice(RunningSliceReadout item)
        {
            return new RunningSliceResponse(
                item.JobId,
                item.ActivityId,
                item.SliceStartUtc,
                item.SliceEndUtc,
                item.Attempt,
                item.LeaseOwner,
                item.LeaseExpiresAtUtc,
                item.LeaseExpired,
                item.LastErrorCode,
                item.LastErrorMessage,
                item.UpdatedAtUtc,
                item.StartedAtUtc,
                item.TotalChunks);
        }

        public static string Lifecycle(JobLifecycleState state)
        {
            return state switch
            {
                JobLifecycleState.Active => "active",
                JobLifecycleState.Paused => "paused",
                JobLifecycleState.SoftDeleted => "softDeleted",
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            };
        }
    }
}
