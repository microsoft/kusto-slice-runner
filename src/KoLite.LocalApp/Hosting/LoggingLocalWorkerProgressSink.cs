// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp
{
    internal sealed class LoggingLocalWorkerProgressSink : ILocalWorkerProgressSink
    {
        private readonly ILogger<LoggingLocalWorkerProgressSink> logger;

        public LoggingLocalWorkerProgressSink(ILogger<LoggingLocalWorkerProgressSink> logger)
        {
            this.logger = logger;
        }

        public void RecordStarted(LocalWorkerProgressEvent progress)
        {
            using (BeginJobScope(progress))
            {
                if (progress.ChunkId is { } chunkId && progress.TotalChunks is { } totalChunks)
                {
                    logger.LogInformation(
                        "Job slice chunk started for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, chunk {ChunkId}/{TotalChunks}, attempt {Attempt}.",
                        JobLabel(progress),
                        progress.SliceStartUtc,
                        progress.SliceEndUtc,
                        chunkId,
                        totalChunks,
                        progress.Attempt);
                    return;
                }

                logger.LogInformation(
                    "Job slice started for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}.",
                    JobLabel(progress),
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt);
            }
        }

        public void RecordFinished(LocalWorkerProgressEvent progress)
        {
            using (BeginJobScope(progress))
            {
                if (progress.Status == LocalWorkerProgressStatus.Succeeded)
                {
                    if (progress.ChunkId is { } chunkId && progress.TotalChunks is { } totalChunks)
                    {
                        logger.LogInformation(
                            "Job slice chunk finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, chunk {ChunkId}/{TotalChunks}, attempt {Attempt}, status {CompletionStatus}.",
                            JobLabel(progress),
                            progress.SliceStartUtc,
                            progress.SliceEndUtc,
                            chunkId,
                            totalChunks,
                            progress.Attempt,
                            progress.Status);
                        return;
                    }

                    logger.LogInformation(
                        "Job slice finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}.",
                        JobLabel(progress),
                        progress.SliceStartUtc,
                        progress.SliceEndUtc,
                        progress.Attempt,
                        progress.Status);
                    return;
                }

                if (progress.Status == LocalWorkerProgressStatus.DeadLettered)
                {
                    if (progress.ChunkId is { } chunkId && progress.TotalChunks is { } totalChunks)
                    {
                        logger.LogError(
                            "Job slice chunk finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, chunk {ChunkId}/{TotalChunks}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                            JobLabel(progress),
                            progress.SliceStartUtc,
                            progress.SliceEndUtc,
                            chunkId,
                            totalChunks,
                            progress.Attempt,
                            progress.Status,
                            progress.ErrorCode,
                            progress.ErrorMessage);
                        return;
                    }

                    logger.LogError(
                        "Job slice finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                        JobLabel(progress),
                        progress.SliceStartUtc,
                        progress.SliceEndUtc,
                        progress.Attempt,
                        progress.Status,
                        progress.ErrorCode,
                        progress.ErrorMessage);
                    return;
                }

                if (progress.ChunkId is { } warningChunkId && progress.TotalChunks is { } warningTotalChunks)
                {
                    logger.LogWarning(
                        "Job slice chunk finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, chunk {ChunkId}/{TotalChunks}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                        JobLabel(progress),
                        progress.SliceStartUtc,
                        progress.SliceEndUtc,
                        warningChunkId,
                        warningTotalChunks,
                        progress.Attempt,
                        progress.Status,
                        progress.ErrorCode,
                        progress.ErrorMessage);
                    return;
                }

                logger.LogWarning(
                    "Job slice finished for job {JobDisplayName}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                    JobLabel(progress),
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt,
                    progress.Status,
                    progress.ErrorCode,
                    progress.ErrorMessage);
            }
        }

        // The human-facing label rendered into the console message. Falls back to the opaque
        // JobId when no display name was resolved, so output degrades gracefully instead of blank.
        private static string JobLabel(LocalWorkerProgressEvent progress) =>
            string.IsNullOrWhiteSpace(progress.DisplayName) ? progress.JobId : progress.DisplayName!;

        // Attaches the durable JobId (GUID) as a structured logging scope property. The default
        // console formatter omits scopes (IncludeScopes is off), so the GUID stays out of the
        // printed text while remaining available to structured sinks and diagnostics.
        private IDisposable? BeginJobScope(LocalWorkerProgressEvent progress)
        {
            var values = new Dictionary<string, object?> { ["JobId"] = progress.JobId };
            if (progress.ChunkId is { } chunkId && progress.TotalChunks is { } totalChunks)
            {
                values["ChunkId"] = chunkId;
                values["TotalChunks"] = totalChunks;
            }

            return logger.BeginScope(values);
        }
    }
}
