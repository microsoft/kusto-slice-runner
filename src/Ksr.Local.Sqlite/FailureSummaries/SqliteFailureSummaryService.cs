// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ksr.Local.Core.FailureSummaries;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Infrastructure;

namespace Ksr.Local.Sqlite.FailureSummaries
{
    public sealed record FailureSummaryRun(string RunId, string? JobId, string Status, string InputHash, string PromptPreview, string? SummaryMarkdown, int FailureCount);

    public sealed class SqliteFailureSummaryService
    {
        private static readonly Regex SecretRegex = new(
            @"(?i)(AccountKey|SharedAccessSignature|sig|Bearer|token|password|secret|client_secret)\s*[:=]\s*[^;\s""']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IKsrSqliteConnectionFactory connectionFactory;
        private readonly IFailureSummaryRunner runner;

        public SqliteFailureSummaryService(IKsrSqliteConnectionFactory connectionFactory, IFailureSummaryRunner runner)
        {
            this.connectionFactory = connectionFactory;
            this.runner = runner;
        }

        public async Task<FailureSummaryRun> SummarizeRecentFailuresAsync(string? jobId = null, int take = 50, CancellationToken cancellationToken = default)
        {
            var failures = ReadFailures(jobId, take);
            var prompt = BuildPrompt(jobId, failures);
            var inputHash = Sha256(prompt);
            var cached = FindCached(jobId, inputHash);
            if (cached is not null) return cached;

            var runId = Guid.NewGuid().ToString("N");
            InsertRun(runId, jobId, inputHash, "Queued", prompt, null, null, 0, failures.Sum(f => f.Count));
            var result = await runner.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
            var summary = result.Succeeded ? Sanitize(result.SummaryMarkdown) : null;
            UpdateRun(runId, inputHash, result.Succeeded ? "Completed" : "Failed", prompt, summary, result.ErrorMessage, result.ExitCode, failures.Sum(f => f.Count));
            return FindRun(runId)!;
        }

        public IReadOnlyList<FailureSummaryRun> ListRuns(string? jobId = null)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT run_id,job_id,summary_json,failure_count FROM failure_summary_runs WHERE ($job IS NULL OR job_id=$job) ORDER BY created_at_utc DESC;");
            cmd.Add("$job", jobId);
            using var r = cmd.ExecuteReader();
            var rows = new List<FailureSummaryRun>();
            while (r.Read()) rows.Add(ReadRun(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3)));
            return rows;
        }

        public static string Sanitize(string value) => SecretRegex.Replace(value, m => m.Groups[1].Value + "=<redacted>");

        private IReadOnlyList<GroupedFailure> ReadFailures(string? jobId, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id,slice_start_utc,slice_end_utc,state,last_error_code,last_error_message,updated_at_utc
                FROM current_slice_state
                WHERE state IN ('Failed','DeadLettered') AND ($job IS NULL OR job_id=$job)
                ORDER BY updated_at_utc DESC LIMIT $take;
                """);
            cmd.Add("$job", jobId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var raw = new List<RawFailure>();
            while (r.Read())
            {
                raw.Add(new RawFailure(
                    r.GetString(0),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    Sanitize(r.IsDBNull(5) ? string.Empty : r.GetString(5)),
                    SqliteStorage.ReadUtc(r, "updated_at_utc")));
            }

            return raw.GroupBy(f => new { Type = f.ErrorCode ?? f.State, Message = Normalize(f.Message) })
                .Select(g => new GroupedFailure(g.Key.Type, g.Key.Message, g.Count(), g.Min(x => x.UpdatedAtUtc), g.Max(x => x.UpdatedAtUtc), g.Select(x => x.JobId).Distinct(StringComparer.Ordinal).Take(5).ToArray(), g.Select(x => $"{x.JobId}|{SqliteStorage.Utc(x.StartUtc)}|{SqliteStorage.Utc(x.EndUtc)}").Take(5).ToArray(), g.Select(x => x.Message).FirstOrDefault() ?? string.Empty))
                .OrderByDescending(g => g.Count)
                .ToArray();
        }

        private static string BuildPrompt(string? jobId, IReadOnlyList<GroupedFailure> failures)
        {
            var json = JsonSerializer.Serialize(failures.Take(20));
            if (json.Length > 6000) json = json[..6000];
            return Sanitize($"""
                You are analyzing recent Kusto Slice Runner job failures from a local SQLite database.
                Summarize likely root causes, recurring patterns, impacted jobs/slices, and recommended next actions.
                Scope: {(jobId is null ? "all jobs" : jobId)}

                Grouped failures:
                {json}
                """);
        }

        private FailureSummaryRun? FindCached(string? jobId, string inputHash)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT run_id,job_id,summary_json,failure_count FROM failure_summary_runs WHERE ($job IS NULL OR job_id=$job) AND summary_json LIKE $hash AND summary_json LIKE '%\"status\":\"Completed\"%' ORDER BY created_at_utc DESC LIMIT 1;");
            cmd.Add("$job", jobId);
            cmd.Add("$hash", $"%\"inputHash\":\"{inputHash}\"%");
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRun(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3)) : null;
        }

        private FailureSummaryRun? FindRun(string runId)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT run_id,job_id,summary_json,failure_count FROM failure_summary_runs WHERE run_id=$id;");
            cmd.Add("$id", runId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRun(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt32(3)) : null;
        }

        private void InsertRun(string runId, string? jobId, string inputHash, string status, string prompt, string? summary, string? error, int exitCode, int failureCount)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                INSERT INTO failure_summary_runs (run_id,job_id,summary_kind,failure_code,failure_count,summary_json)
                VALUES ($id,$job,'recent-failures',$hash,$count,$json);
                """);
            cmd.Add("$id", runId); cmd.Add("$job", jobId); cmd.Add("$hash", inputHash); cmd.Add("$count", failureCount); cmd.Add("$json", Payload(inputHash, status, prompt, summary, error, exitCode)); cmd.ExecuteNonQuery();
        }

        private void UpdateRun(string runId, string inputHash, string status, string prompt, string? summary, string? error, int exitCode, int failureCount)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "UPDATE failure_summary_runs SET failure_count=$count, summary_json=$json, updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE run_id=$id;");
            cmd.Add("$id", runId); cmd.Add("$count", failureCount); cmd.Add("$json", Payload(inputHash, status, prompt, summary, error, exitCode)); cmd.ExecuteNonQuery();
        }

        private static string Payload(string inputHash, string status, string prompt, string? summary, string? error, int exitCode) =>
            JsonSerializer.Serialize(new { inputHash, status, promptPreview = prompt.Length > 1000 ? prompt[..1000] : prompt, summaryMarkdown = summary, errorMessage = error, exitCode }, SqliteStorage.JsonOptions);

        private static FailureSummaryRun ReadRun(string runId, string? jobId, string json, int count)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new FailureSummaryRun(
                runId,
                jobId,
                root.GetProperty("status").GetString()!,
                root.GetProperty("inputHash").GetString()!,
                root.GetProperty("promptPreview").GetString()!,
                root.TryGetProperty("summaryMarkdown", out var summary) && summary.ValueKind != JsonValueKind.Null ? summary.GetString() : null,
                count);
        }

        private static string Normalize(string message) => Regex.Replace(Sanitize(message), @"\d+", "#", RegexOptions.CultureInvariant).Trim();
        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private sealed record RawFailure(string JobId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string State, string? ErrorCode, string Message, DateTimeOffset UpdatedAtUtc);
        private sealed record GroupedFailure(string ErrorType, string NormalizedMessage, int Count, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, string[] SampleJobIds, string[] SampleSliceKeys, string RepresentativeMessage);
    }
}
