// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Observability;

namespace Ksr.LocalApp.Ui
{
    public sealed record SliceAttemptReadout(
        string AttemptId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string Status,
        string? WorkerId,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? ErrorCode,
        string? ErrorMessage,
        int? ChunkId,
        int? TotalChunks);

    public sealed record OperationalLogReadout(
        string LogId,
        string? JobId,
        DateTimeOffset? SliceStartUtc,
        DateTimeOffset? SliceEndUtc,
        string Level,
        string Message,
        string? Category,
        string? Exception,
        DateTimeOffset RecordedAtUtc,
        int? ChunkId,
        int? TotalChunks);

    public sealed record SliceEventReadout(
        string EventId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string EventType,
        string? State,
        int? Attempt,
        string? Reason,
        string? Actor,
        DateTimeOffset RecordedAtUtc);

    public sealed class OperationalDetailsReadModel
    {
        private readonly SqliteOperationalReadModelRepository repository;

        public OperationalDetailsReadModel(SqliteOperationalReadModelRepository repository)
        {
            this.repository = repository;
        }

        public IReadOnlyList<SliceAttemptReadout> GetAttempts(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50) =>
            repository.GetSliceAttempts(jobId, sliceStartUtc, sliceEndUtc, take)
                .Select(row => new SliceAttemptReadout(
                    row.AttemptId,
                    row.JobId,
                    row.SliceStartUtc,
                    row.SliceEndUtc,
                    row.Attempt,
                    row.Status,
                    row.WorkerId,
                    row.StartedAtUtc,
                    row.CompletedAtUtc,
                    row.ErrorCode,
                    row.ErrorMessage,
                    row.ChunkId,
                    row.TotalChunks))
                .ToList();

        public IReadOnlyList<OperationalLogReadout> GetLogs(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50) =>
            repository.GetOperationalLogs(jobId, sliceStartUtc, sliceEndUtc, take)
                .Select(row => new OperationalLogReadout(
                    row.LogId,
                    row.JobId,
                    row.SliceStartUtc,
                    row.SliceEndUtc,
                    row.Level,
                    row.Message,
                    row.Category,
                    row.Exception,
                    row.RecordedAtUtc,
                    row.ChunkId,
                    row.TotalChunks))
                .ToList();

        public IReadOnlyList<SliceEventReadout> GetEvents(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50) =>
            repository.GetSliceStateEvents(jobId, sliceStartUtc, sliceEndUtc, take)
                .Select(row => new SliceEventReadout(
                    row.EventId,
                    row.JobId,
                    row.SliceStartUtc,
                    row.SliceEndUtc,
                    row.EventType,
                    row.State,
                    row.Attempt,
                    row.Reason,
                    row.Actor,
                    row.RecordedAtUtc))
                .ToList();
    }
}
