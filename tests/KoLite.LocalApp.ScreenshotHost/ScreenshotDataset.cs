// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KoLite.Local.Core.FailureSummaries;
using KoLite.Local.Core.Graph;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;

namespace KoLite.LocalApp.ScreenshotHost
{
    public sealed record ScreenshotJob(
        int Number,
        string ActivityId,
        string FunctionName,
        string OutputTable,
        TimeSpan Window,
        string[] Tags,
        int[] DependsOn,
        int? Chunks = null,
        bool IsPaused = false,
        int Days = 2)
    {
        public string Id => ScreenshotDataset.JobId(Number);
        public DateTimeOffset Start => Window == TimeSpan.FromDays(1)
            ? ScreenshotDataset.DailyEndUtc.AddDays(-Days)
            : ScreenshotDataset.Now.AddDays(-Days);
    }

    public static class ScreenshotDataset
    {
        private static readonly DateTimeOffset CaptureStartedUtc = DateTimeOffset.UtcNow;
        // The history view also checks wall-clock lease expiry. Freeze a recent anchor, not an old date.
        public static readonly DateTimeOffset Now = new(CaptureStartedUtc.Year, CaptureStartedUtc.Month, CaptureStartedUtc.Day,
            CaptureStartedUtc.Hour, CaptureStartedUtc.Minute / 5 * 5, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset DailyEndUtc = new(Now.AddMinutes(-9).Date, TimeSpan.Zero);
        public const string Cluster = "https://kolite-example.invalid";
        public const string Database = "RetailDemo";
        public const string FailureMessage = "Query schema does not match table schema. RefundAmount is long; target RefundAmount is real.";
        public static string DetailJobId => JobId(5);
        public static string FailureJobId => JobId(9);
        private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
        private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static IReadOnlyList<ScreenshotJob> Jobs { get; } = new ScreenshotJob[]
        {
            new(1, "Demo.Orders5Min", "BuildOrders5Min", "OrderFacts", FiveMinutes, ["retail", "orders"], [], Chunks: 4),
            new(2, "Demo.Inventory5Min", "BuildInventory5Min", "InventorySnapshots", FiveMinutes, ["retail", "inventory"], []),
            new(3, "Demo.Shipments5Min", "BuildShipments5Min", "ShipmentFacts", FiveMinutes, ["retail", "fulfillment"], [1]),
            new(4, "Demo.Customers5Min", "BuildCustomers5Min", "CustomerMetrics", FiveMinutes, ["retail", "customers"], [1]),
            new(5, "Demo.Revenue5Min", "BuildRevenue5Min", "RevenueMetrics", FiveMinutes, ["retail", "sales"], [1]),
            new(6, "Demo.Fulfillment5Min", "BuildFulfillment5Min", "FulfillmentMetrics", FiveMinutes, ["retail", "fulfillment"], [2, 3]),
            new(7, "Demo.SalesByRegion5Min", "BuildSalesByRegion5Min", "RegionalSales", FiveMinutes, ["retail", "sales"], [5]),
            new(8, "Demo.DailySummary", "BuildDailySummary", "DailySales", TimeSpan.FromDays(1), ["retail", "daily"], [], Days: 40),
            new(9, "Demo.RefundsDaily", "BuildRefundsDaily", "DailyRefunds", TimeSpan.FromDays(1), ["retail", "daily"], [8], Days: 12),
            new(10, "Demo.ForecastHourly", "BuildForecastHourly", "SalesForecast", TimeSpan.FromHours(1), ["retail", "forecast"], [], IsPaused: true)
        };

        public static string JobId(int number) => "10000000000040008000" + number.ToString("D12", CultureInfo.InvariantCulture);

        public static void Seed(IKoLiteSqliteConnectionFactory connections)
        {
            var catalog = new SqliteJobCatalogRepository(connections);
            if (catalog.List().Count != 0)
            {
                throw new InvalidOperationException("Screenshot fixtures require an empty, newly created database.");
            }

            var slices = new SqliteSliceStateRepository(connections);
            var chunks = new SqliteChunkStateRepository(connections);
            var attempts = new SqliteOperationalReadModelRepository(connections);
            var queue = new SqliteWorkQueueRepository(connections);
            foreach (var job in Jobs)
            {
                catalog.Create(Schedule(job), actor: "synthetic-fixture", eventId: $"create-{job.Number}");
                var cutoff = job.Window == FiveMinutes
                    ? Now.AddMinutes(job.Number is 5 or 7 ? -15 : -10)
                    : job.Window == TimeSpan.FromDays(1)
                        ? DailyEndUtc
                        : Now.AddHours(-1);
                for (var start = job.Start; start < cutoff; start += job.Window)
                {
                    var slice = new SliceRange(job.Id, start, start + job.Window);
                    var lagMinutes = job.Number switch { 1 => 2, 2 => 3, 3 or 4 => 5, 5 => 7, 6 or 9 => 8, 7 => 10, _ => 5 };
                    var queueDelay = job.Window == FiveMinutes && start < Now.AddHours(-2)
                        ? 10 + 8 * Math.Sin((start - Now).TotalHours * Math.PI / 3)
                        : 0;
                    var completed = slice.EndUtc.AddMinutes(lagMinutes + queueDelay);
                    var duration = TimeSpan.FromSeconds(65 + (start.Hour * 11 + start.Minute + job.Number * 7) % 85);
                    var failed = job.Number == 9 && start >= DailyEndUtc.AddDays(-3);
                    if (job.Chunks is { } count)
                    {
                        foreach (var child in chunks.EnsureWindow(slice, count, "synthetic-fixture"))
                        {
                            CompleteChunk(chunks, attempts, child, completed.AddSeconds(child.ChunkId * 8), duration);
                        }
                    }
                    else
                    {
                        var retried = job.Number == 5 && start == Now.AddHours(-6).AddMinutes(-15);
                        CompleteSlice(slices, attempts, slice, completed, duration, failed, retried);
                    }
                    if (failed)
                    {
                        attempts.RecordLog("Error", FailureMessage, category: "synthetic-fixture",
                            jobId: job.Id, sliceStartUtc: slice.StartUtc, sliceEndUtc: slice.EndUtc);
                    }
                }
            }

            var orderWindow = new SliceRange(JobId(1), Now.AddMinutes(-10), Now.AddMinutes(-5));
            foreach (var child in chunks.EnsureWindow(orderWindow, 4, "synthetic-fixture"))
            {
                if (child.ChunkId < 2)
                {
                    CompleteChunk(chunks, attempts, child, Now.AddMinutes(-2).AddSeconds(child.ChunkId * 15), TimeSpan.FromMinutes(1.5));
                }
                else
                {
                    var started = Now.AddSeconds(-75 + child.ChunkId * 5);
                    var worker = $"demo-worker-{child.ChunkId + 1}";
                    chunks.MarkQueued(Key(child.Execution) + "-queued", child.Execution, actor: "synthetic-fixture");
                    var lease = chunks.AcquireLease(Key(child.Execution) + "-running", child.Execution, worker, LeaseDuration, started)
                        ?? throw new InvalidOperationException("Could not acquire synthetic chunk lease.");
                    RecordAttempt(attempts, child.Execution, lease.Attempt, "Started", worker, started, null);
                    Enqueue(queue, child.Execution, started, worker);
                }
            }

            var revenueWindow = new SliceRange(DetailJobId, Now.AddMinutes(-15), Now.AddMinutes(-10));
            var revenueLease = slices.AcquireLease("revenue-running", DetailJobId, revenueWindow.StartUtc, revenueWindow.EndUtc,
                "demo-worker-1", LeaseDuration, Now.AddMinutes(-1)) ?? throw new InvalidOperationException("Could not acquire synthetic slice lease.");
            RecordAttempt(attempts, SliceExecutionUnit.Unchunked(revenueWindow), revenueLease.Attempt, "Started", "demo-worker-1", Now.AddMinutes(-1), null);
            Enqueue(queue, SliceExecutionUnit.Unchunked(revenueWindow), Now.AddMinutes(-1), "demo-worker-1");

            var queuedOrders = new SliceRange(JobId(1), Now.AddMinutes(-5), Now);
            foreach (var child in chunks.EnsureWindow(queuedOrders, 4, "synthetic-fixture").Take(2))
            {
                chunks.MarkQueued(Key(child.Execution) + "-queued", child.Execution, actor: "synthetic-fixture");
                Enqueue(queue, child.Execution, Now);
            }
            var inventoryWindow = new SliceRange(JobId(2), Now.AddMinutes(-10), Now.AddMinutes(-5));
            slices.Append("inventory-queued", inventoryWindow.JobId, inventoryWindow.StartUtc, inventoryWindow.EndUtc,
                DurableSliceStatus.Queued, 0, actor: "synthetic-fixture");
            Enqueue(queue, SliceExecutionUnit.Unchunked(inventoryWindow), Now.AddMinutes(-1));

            foreach (var number in new[] { 3, 4, 5, 6, 7 })
            {
                for (var start = Now.AddMinutes(number == 7 ? -15 : -10); start < Now; start += FiveMinutes)
                {
                    slices.Append($"waiting-{number}-{start.Ticks}", JobId(number), start, start + FiveMinutes,
                        DurableSliceStatus.DependencyBlocked, 0, reason: "Waiting for synthetic upstream window.", actor: "synthetic-fixture");
                }
            }

            // Some repositories stamp wall-clock creation/queue times. Normalize only this fresh fixture.
            using var connection = connections.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE job_definitions SET created_at_utc=$created, updated_at_utc=$created;
                UPDATE job_definition_events SET recorded_at_utc=$created;
                UPDATE current_slice_state SET updated_at_utc=$now WHERE updated_at_utc > $now;
                UPDATE slice_state_events SET recorded_at_utc=$now WHERE recorded_at_utc > $now;
                UPDATE current_slice_chunk_state SET updated_at_utc=$now WHERE updated_at_utc > $now;
                UPDATE slice_chunk_state_events SET recorded_at_utc=$now WHERE recorded_at_utc > $now;
                UPDATE work_queue SET created_at_utc=available_at_utc, updated_at_utc=MIN(updated_at_utc, $now);
                UPDATE operational_logs SET recorded_at_utc=slice_end_utc;
                """;
            command.Parameters.AddWithValue("$created", Utc(Now.AddDays(-40)));
            command.Parameters.AddWithValue("$now", Utc(Now));
            command.ExecuteNonQuery();
        }

        private static string Schedule(ScreenshotJob job) => JsonSerializer.Serialize(new
        {
            id = job.Id,
            activityId = job.ActivityId,
            functionName = job.FunctionName,
            outputTable = job.OutputTable,
            queryWindowSize = job.Window.ToString("c", CultureInfo.InvariantCulture),
            delayFromUtcNow = "00:00:00",
            maxParallelism = job.Chunks ?? 2,
            queryTimeout = "00:10:00",
            chunks = job.Chunks,
            isPaused = job.IsPaused,
            startFrom = Utc(job.Start),
            tags = job.Tags,
            dependsOn = job.DependsOn.Select(number => new { id = JobId(number) }),
            target = new { clusterUri = Cluster, database = Database }
        }, JsonOptions);

        private static void CompleteSlice(SqliteSliceStateRepository state, SqliteOperationalReadModelRepository attempts,
            SliceRange slice, DateTimeOffset completed, TimeSpan duration, bool failed, bool retried)
        {
            var execution = SliceExecutionUnit.Unchunked(slice);
            var key = Key(execution);
            const string worker = "demo-history-worker";
            if (retried)
            {
                var firstStart = completed - duration - TimeSpan.FromMinutes(1);
                var first = state.AcquireLease(key + "-lease-1", slice.JobId, slice.StartUtc, slice.EndUtc, worker, LeaseDuration, firstStart)
                    ?? throw new InvalidOperationException("Could not acquire synthetic retry lease.");
                Require(state.FailLease(key + "-retry", slice.JobId, slice.StartUtc, slice.EndUtc, worker, first.LeaseToken!,
                    firstStart.AddSeconds(10), "Synthetic transient query timeout."));
                RecordAttempt(attempts, execution, 1, "FailedRetryable", worker, firstStart, firstStart.AddSeconds(10),
                    "QueryTimeout", "Synthetic transient query timeout.");
            }

            var lease = state.AcquireLease(key + "-lease", slice.JobId, slice.StartUtc, slice.EndUtc, worker, LeaseDuration, completed - duration)
                ?? throw new InvalidOperationException("Could not acquire synthetic historical lease.");
            Require(failed
                ? state.DeadLetterLease(key + "-done", slice.JobId, slice.StartUtc, slice.EndUtc, worker, lease.LeaseToken!, completed, FailureMessage)
                : state.CompleteLease(key + "-done", slice.JobId, slice.StartUtc, slice.EndUtc, worker, lease.LeaseToken!, completed));
            RecordAttempt(attempts, execution, lease.Attempt, failed ? "DeadLettered" : "Succeeded", worker, completed - duration, completed,
                failed ? "SchemaMismatch" : null, failed ? FailureMessage : null);
        }

        private static void CompleteChunk(SqliteChunkStateRepository state, SqliteOperationalReadModelRepository attempts,
            DurableChunkState child, DateTimeOffset completed, TimeSpan duration)
        {
            var execution = child.Execution;
            var key = Key(execution);
            const string worker = "demo-history-worker";
            var lease = state.AcquireLease(key + "-lease", execution, worker, LeaseDuration, completed - duration)
                ?? throw new InvalidOperationException("Could not acquire synthetic historical chunk lease.");
            Require(state.CompleteLease(key + "-done", execution, worker, lease.LeaseToken!, completed));
            RecordAttempt(attempts, execution, lease.Attempt, "Succeeded", worker, completed - duration, completed);
        }

        private static void Enqueue(SqliteWorkQueueRepository queue, SliceExecutionUnit execution, DateTimeOffset available, string? worker = null)
        {
            var item = queue.Enqueue(execution.Slice.JobId, execution.Slice.StartUtc, execution.Slice.EndUtc,
                Key(execution), available, chunkId: execution.ChunkId, totalChunks: execution.TotalChunks);
            if (worker is not null)
            {
                var claimed = queue.ClaimQueued("default", worker, LeaseDuration, available, enforceJobParallelism: true);
                Require(claimed?.QueueItemId == item.QueueItemId);
            }
        }

        private static void RecordAttempt(SqliteOperationalReadModelRepository attempts, SliceExecutionUnit execution, int attempt,
            string status, string worker, DateTimeOffset started, DateTimeOffset? completed, string? errorCode = null, string? error = null) =>
            attempts.RecordAttempt($"{Key(execution)}-attempt-{attempt}", execution.Slice.JobId, execution.Slice.StartUtc, execution.Slice.EndUtc,
                attempt, status, worker, started, completed, errorCode, error, chunkId: execution.ChunkId, totalChunks: execution.TotalChunks);

        private static string Key(SliceExecutionUnit execution) =>
            $"{execution.Slice.JobId}-{execution.Slice.StartUtc.Ticks}-{execution.ChunkId?.ToString(CultureInfo.InvariantCulture) ?? "slice"}";

        private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Synthetic state transition failed.");
        }
    }

    public sealed class ScreenshotLineageReader : IKustoEntityDependencyReader
    {
        public Task<IReadOnlyList<KustoEntityEdge>> ReadAsync(Uri clusterUri, string database, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clusterUri != new Uri(ScreenshotDataset.Cluster) || database != ScreenshotDataset.Database)
            {
                throw new InvalidOperationException("Only the synthetic screenshot target has fixture lineage.");
            }

            var edges = new List<KustoEntityEdge>();
            foreach (var job in ScreenshotDataset.Jobs)
            {
                foreach (var dependency in job.DependsOn)
                {
                    edges.Add(Edge(job.FunctionName, ScreenshotDataset.Jobs.Single(upstream => upstream.Number == dependency).OutputTable, "Table"));
                }
            }
            edges.AddRange(new[]
            {
                Edge("BuildOrders5Min", "RawOrders", "Table"),
                Edge("BuildInventory5Min", "RawInventory", "Table"),
                Edge("BuildShipments5Min", "RawShipments", "Table"),
                Edge("BuildCustomers5Min", "CustomerDirectory", "Table"),
                Edge("BuildRevenue5Min", "ExchangeRates", "Table", dependencyDatabase: "ReferenceData"),
                Edge("BuildSalesByRegion5Min", "InventorySnapshots", "Table"),
                Edge("LatestRevenue", "RevenueMetrics", "Table"),
                Edge("SalesDashboard", "LatestRevenue", "Function"),
                Edge("LatestInventory", "InventorySnapshots", "Table"),
                Edge("LatestFulfillment", "FulfillmentMetrics", "Table")
            });
            return Task.FromResult<IReadOnlyList<KustoEntityEdge>>(edges);
        }

        private static KustoEntityEdge Edge(string source, string dependency, string dependencyType, string? dependencyDatabase = null) =>
            new(new Uri(ScreenshotDataset.Cluster).Host, ScreenshotDataset.Database, source, "Function",
                new Uri(ScreenshotDataset.Cluster).Host, dependencyDatabase ?? ScreenshotDataset.Database, dependency, dependencyType);
    }

    public sealed class ScreenshotAnalysisRunner : IFailureSummaryRunner
    {
        public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prompt.Contains("Demo.RefundsDaily", StringComparison.Ordinal)
                || !prompt.Contains("deadLettered: 3", StringComparison.Ordinal)
                || !prompt.Contains(ScreenshotDataset.FailureMessage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The illustrative analysis must match the synthetic refund failure evidence.");
            }
            var firstDay = ScreenshotDataset.DailyEndUtc.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastDay = ScreenshotDataset.DailyEndUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Task.FromResult(FailureSummaryRunnerResult.Success($$"""
                ## Kusto Slice Runner Job Failure Analysis

                > Illustrative analysis of synthetic data. No Copilot request was made.

                ### Verdict
                The latest three daily windows failed because the query output type does not match the destination schema. Retrying the same query will not fix this.

                ### Impact
                - **Job:** Demo.RefundsDaily
                - **Affected windows:** {{firstDay}} through {{lastDay}} (UTC)
                - **Outcome:** 3 dead-lettered windows; the preceding 9 windows completed successfully.
                - **Target:** RetailDemo / DailyRefunds on `kolite-example.invalid`

                ### Root cause
                `BuildRefundsDaily` returns `RefundAmount` as **long**, but `DailyRefunds` expects **real**. Every affected attempt contains the same schema-mismatch error:

                > Query schema does not match table schema. RefundAmount is long; target RefundAmount is real.

                This is a query/schema contract issue, not evidence of a capacity problem or an upstream outage.

                ### Recovery
                1. Compare the function's result columns and types with the target schema.
                2. Correct the type mismatch, then validate the function on a small synthetic window.
                3. Repair the three failed windows and confirm that each completes. Preserve the successful history.

                **Retryable?** Not until the schema mismatch is corrected.
                """));
        }
    }
}
