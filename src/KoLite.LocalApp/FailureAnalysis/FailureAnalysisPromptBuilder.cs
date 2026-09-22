// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.FailureSummaries;
using KoLite.Local.Sqlite.Observability;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.FailureAnalysis
{
    // The gathered, secret-sanitized failure evidence for one job plus the ready-to-send user prompt.
    // FailureCount == 0 means there is nothing to analyze, so the endpoint can short-circuit without a
    // model call.
    public sealed record FailureEvidence(string Prompt, int FailureCount);

    // Builds the failure-analysis prompt for a single job from Kusto Slice Runner's local read models. This runs
    // inside the request scope (it uses scoped repositories) and produces a plain immutable string, so
    // the slow model call can happen later on a background task without touching scoped services.
    public sealed class FailureAnalysisPromptBuilder
    {
        private const int MaxSlicesPerState = 15;
        private const int MaxAttempts = 20;
        private const int MaxMessageChars = 500;
        private const int MaxPromptChars = 12000;

        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;
        private readonly OperationalDetailsReadModel operationalDetails;
        private readonly IClock clock;

        public FailureAnalysisPromptBuilder(
            SqliteOperationalReadModelRepository readModels,
            SqliteDiagnosticsReadModelRepository diagnostics,
            OperationalDetailsReadModel operationalDetails,
            IClock clock)
        {
            this.readModels = readModels;
            this.diagnostics = diagnostics;
            this.operationalDetails = operationalDetails;
            this.clock = clock;
        }

        public FailureEvidence Build(JobCatalogRecord job)
        {
            var definition = job.Definition;
            var now = clock.UtcNow;

            var summary = readModels.GetJobStatusSummaries().FirstOrDefault(s => s.JobId == job.JobId);
            var failed = diagnostics.GetSlices(job.JobId, "Failed", null, null, now, MaxSlicesPerState);
            var deadLettered = diagnostics.GetSlices(job.JobId, "DeadLettered", null, null, now, MaxSlicesPerState);
            var attempts = operationalDetails.GetAttempts(job.JobId, null, null, MaxAttempts);
            var failedAttempts = attempts
                .Where(a => !string.IsNullOrWhiteSpace(a.ErrorCode) || !string.IsNullOrWhiteSpace(a.ErrorMessage))
                .ToList();

            var failureCount = failed.Count + deadLettered.Count + failedAttempts.Count;

            var builder = new StringBuilder();
            builder.AppendLine("Analyze the recent failures for this Kusto Slice Runner job and explain the likely root cause.");
            builder.AppendLine();
            builder.AppendLine("## Job");
            builder.AppendLine(Invariant($"- activityId: {definition.ActivityId}"));
            builder.AppendLine(Invariant($"- id: {job.JobId}"));
            builder.AppendLine(Invariant($"- target cluster: {definition.Target.ClusterUri}"));
            builder.AppendLine(Invariant($"- target database: {definition.Target.Database}"));
            builder.AppendLine(Invariant($"- function: {definition.FunctionName}"));
            builder.AppendLine(Invariant($"- output table: {definition.OutputTable}"));
            builder.AppendLine(Invariant(
                $"- queryWindowSize: {definition.QueryWindowSize}, delayFromUtcNow: {definition.DelayFromUtcNow}, maxParallelism: {definition.MaxParallelism}, queryTimeout: {definition.QueryTimeout}"));

            builder.AppendLine();
            builder.AppendLine("## Slice-state counts");
            if (summary is null)
            {
                builder.AppendLine("- (no slice-state summary available)");
            }
            else
            {
                builder.AppendLine(Invariant(
                    $"- completed: {summary.CompletedCount}, running: {summary.RunningCount}, failed: {summary.FailedCount}, deadLettered: {summary.DeadLetteredCount}, dependencyBlocked: {summary.DependencyBlockedCount}, queued: {summary.QueuedCount}"));
            }

            AppendSlices(builder, "Failed slices (current, retry-pending)", failed);
            AppendSlices(builder, "Dead-lettered slices (current, retries exhausted)", deadLettered);
            AppendAttempts(builder, failedAttempts);

            var prompt = builder.ToString();
            if (prompt.Length > MaxPromptChars)
            {
                prompt = prompt[..MaxPromptChars] + "\n... (evidence truncated)";
            }

            return new FailureEvidence(prompt, failureCount);
        }

        private static void AppendSlices(StringBuilder builder, string heading, IReadOnlyList<SliceStateReadout> slices)
        {
            builder.AppendLine();
            builder.AppendLine(Invariant($"## {heading} ({slices.Count})"));
            if (slices.Count == 0)
            {
                builder.AppendLine("- (none)");
                return;
            }

            foreach (var slice in slices)
            {
                var code = string.IsNullOrWhiteSpace(slice.LastErrorCode) ? "(no code)" : Clip(SqliteFailureSummaryService.Sanitize(slice.LastErrorCode), 120);
                var message = Clip(SqliteFailureSummaryService.Sanitize(slice.LastErrorMessage ?? string.Empty), MaxMessageChars);
                builder.AppendLine(Invariant(
                    $"- {Iso(slice.SliceStartUtc)}..{Iso(slice.SliceEndUtc)} attempt {slice.Attempt} [{code}] {message}"));
            }
        }

        private static void AppendAttempts(StringBuilder builder, IReadOnlyList<SliceAttemptReadout> attempts)
        {
            builder.AppendLine();
            builder.AppendLine(Invariant($"## Recent failed attempts ({attempts.Count})"));
            if (attempts.Count == 0)
            {
                builder.AppendLine("- (none)");
                return;
            }

            foreach (var attempt in attempts)
            {
                var code = string.IsNullOrWhiteSpace(attempt.ErrorCode) ? "(no code)" : Clip(SqliteFailureSummaryService.Sanitize(attempt.ErrorCode), 120);
                var message = Clip(SqliteFailureSummaryService.Sanitize(attempt.ErrorMessage ?? string.Empty), MaxMessageChars);
                builder.AppendLine(Invariant(
                    $"- {Iso(attempt.SliceStartUtc)} attempt {attempt.Attempt} {attempt.Status} started {IsoOrDash(attempt.StartedAtUtc)} completed {IsoOrDash(attempt.CompletedAtUtc)} [{code}] {message}"));
            }
        }

        private static string Clip(string value, int max)
        {
            var collapsed = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
        }

        private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        private static string IsoOrDash(DateTimeOffset? value) => value is null ? "-" : Iso(value.Value);
        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
