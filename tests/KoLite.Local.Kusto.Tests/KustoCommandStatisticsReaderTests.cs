using System.Data;
using System.Globalization;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using KoLite.Local.Core.Performance;
using KoLite.Local.Kusto.Execution;

namespace KoLite.Local.Kusto.Tests
{
    public sealed class KustoCommandStatisticsReaderTests
    {
        private const string RequestId = "KoLite.Local.Output;attempt-1";
        private static readonly Uri Cluster = new("https://statistics.example.invalid");
        private static readonly Guid ActivityId = Guid.Parse("19f5c377-4bfd-4944-9ae1-4c438415c31f");
        private static readonly string[] ColumnNames =
        [
            "ClientActivityId", "RootActivityId", "StartedOn", "LastUpdatedOn",
            "State", "TotalCpu", "Duration", "MemoryPeak"
        ];

        [Fact]
        public async Task Reader_uses_the_supplied_database_factory_and_a_fixed_bounded_projection()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            var query = Query() with
            {
                Database = "Recorded Database",
                FromUtc = At(-60).ToOffset(TimeSpan.FromHours(2)),
                ToUtc = At(3600).ToOffset(TimeSpan.FromHours(-7)),
                ClientRequestIds = [RequestId, "KoLite.Local.Output;attempt-2"],
                MaxResults = 17
            };

            var result = await new KustoCommandStatisticsReader(factory).ReadAsync(query);

            Assert.Empty(result);
            Assert.Equal(1, factory.CreateCount);
            Assert.Same(Cluster, factory.ClusterUri);
            Assert.Equal(query.Database, factory.Database);
            Assert.Equal(query.Database, factory.Client.Database);
            Assert.Equal("""
                .show commands-and-queries
                | where Database == "Recorded Database"
                    and CommandType == "TableSetOrAppend"
                    and StartedOn >= datetime(2025-12-31T23:59:00.0000000Z)
                    and StartedOn < datetime(2026-01-01T01:00:00.0000000Z)
                    and ClientActivityId in ("KoLite.Local.Output;attempt-1", "KoLite.Local.Output;attempt-2")
                | project ClientActivityId,
                          RootActivityId,
                          StartedOn,
                          LastUpdatedOn,
                          State,
                          TotalCpu,
                          Duration,
                          MemoryPeak
                | take 18
                """, factory.Client.CommandText);
            Assert.DoesNotContain("Text", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("Principal", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("ClientRequestProperties", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("State ==", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.True(wire.Exhausted);
            Assert.True(wire.Disposed);
            Assert.True(factory.Client.Disposed);
        }

        [Fact]
        public async Task Reader_escapes_quotes_backslashes_line_breaks_controls_and_unicode_inside_literals()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            var query = Query() with
            {
                Database = "quoted\"\\\r\n\t\u0001\u00e9",
                ClientRequestIds = ["request\"\\\n'); .show version //", "quote'\u0000\u2028"]
            };

            await new KustoCommandStatisticsReader(factory).ReadAsync(query);

            Assert.Contains("Database == \"quoted\\\"\\\\\\r\\n\\t\\u0001\\u00e9\"", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.Contains(
                "ClientActivityId in (\"request\\\"\\\\\\n'); .show version //\", \"quote'\\u0000\\u2028\")",
                factory.Client.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(query.Database, factory.Client.CommandText, StringComparison.Ordinal);
            Assert.Equal(query.Database, factory.Database);
            Assert.Equal(query.Database, factory.Client.Database);
        }

        [Fact]
        public async Task Reader_sets_a_30_second_deadline_and_does_not_defer_partial_failures()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);

            await new KustoCommandStatisticsReader(factory).ReadAsync(Query());

            var properties = Assert.IsType<ClientRequestProperties>(factory.Client.Properties);
            Assert.Equal(TimeSpan.FromSeconds(30), properties.GetOptionValueOrDefault(ClientRequestProperties.OptionServerTimeout, TimeSpan.Zero));
            Assert.False(properties.GetOptionValueOrDefault(ClientRequestProperties.OptionDeferPartialQueryFailures, true));
            Assert.StartsWith("KoLite.Local.Performance;", properties.ClientRequestId, StringComparison.Ordinal);
            Assert.NotEqual(RequestId, properties.ClientRequestId);
            Assert.True(factory.Client.CancellationToken.CanBeCanceled);
        }

        [Fact]
        public async Task Reader_maps_documented_sdk_types_to_seconds_and_int64_bytes()
        {
            using var table = StatisticsTable();
            Type[] types = [typeof(string), typeof(Guid), typeof(DateTime), typeof(DateTime), typeof(string), typeof(TimeSpan), typeof(TimeSpan), typeof(long)];
            for (var i = 0; i < types.Length; i++)
            {
                table.Columns[i].DataType = types[i];
            }

            table.Rows.Add(ValidRow());
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);

            var row = Assert.Single(await new KustoCommandStatisticsReader(factory).ReadAsync(Query()));

            Assert.Equal(RequestId, row.ClientRequestId);
            Assert.Equal(ActivityId, row.ServerActivityId);
            Assert.Equal(At(0), row.StartedAtUtc);
            Assert.Equal(At(5), row.CompletedAtUtc);
            Assert.Equal("Completed", row.State);
            Assert.Equal(8.125d, row.CpuSeconds);
            Assert.Equal(1.75d, row.DurationSeconds);
            Assert.Equal(8589934593L, row.MemoryPeakBytes);
            Assert.Null(row.ValidationError);
        }

        [Fact]
        public async Task Reader_parses_invariant_timespans_and_integers_without_substituting_local_elapsed_time()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var values = ValidRow();
                values[5] = "1.02:03:04.1234567";
                values[6] = "00:00:00.0000001";
                values[7] = "9223372036854775807";
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Equal(TimeSpan.ParseExact("1.02:03:04.1234567", "c", CultureInfo.InvariantCulture).TotalSeconds, row.CpuSeconds);
                Assert.Equal(0.0000001d, row.DurationSeconds);
                Assert.Equal(long.MaxValue, row.MemoryPeakBytes);
                Assert.Null(row.ValidationError);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        [Fact]
        public async Task Reader_normalizes_utc_unspecified_local_offset_and_iso_timestamp_values()
        {
            object[] starts =
            [
                At(0).UtcDateTime,
                DateTime.SpecifyKind(At(0).UtcDateTime, DateTimeKind.Unspecified),
                At(0).LocalDateTime,
                At(0).ToOffset(TimeSpan.FromHours(2)),
                "2026-01-01T00:00:00.0000000Z",
                "2026-01-01T02:00:00+02:00",
                "2026-01-01T00:00:00"
            ];
            foreach (var start in starts)
            {
                var values = ValidRow();
                values[1] = ActivityId.ToString("D");
                values[2] = start;
                values[3] = "2025-12-31T17:00:05-07:00";
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Equal(ActivityId, row.ServerActivityId);
                Assert.Equal(At(0), row.StartedAtUtc);
                Assert.Equal(At(5), row.CompletedAtUtc);
                Assert.Equal(TimeSpan.Zero, row.StartedAtUtc.Offset);
                Assert.Equal(TimeSpan.Zero, row.CompletedAtUtc.Offset);
            }
        }

        [Fact]
        public async Task Reader_preserves_genuine_zero_metrics()
        {
            var values = ValidRow();
            values[5] = TimeSpan.Zero;
            values[6] = "00:00:00";
            values[7] = 0L;
            using var table = StatisticsTable(values);
            using var wire = new TrackingDataReader(table.CreateDataReader());

            var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Equal(0d, row.CpuSeconds);
            Assert.Equal(0d, row.DurationSeconds);
            Assert.Equal(0L, row.MemoryPeakBytes);
            Assert.Null(row.ValidationError);
        }

        [Fact]
        public async Task Reader_keeps_missing_metrics_null_with_explicit_per_family_errors()
        {
            var values = ValidRow();
            values[5] = DBNull.Value;
            values[6] = DBNull.Value;
            values[7] = DBNull.Value;
            using var table = StatisticsTable(values);
            using var wire = new TrackingDataReader(table.CreateDataReader());

            var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Null(row.CpuSeconds);
            Assert.Null(row.DurationSeconds);
            Assert.Null(row.MemoryPeakBytes);
            Assert.Contains("TotalCpu", row.ValidationError, StringComparison.Ordinal);
            Assert.Contains("Duration", row.ValidationError, StringComparison.Ordinal);
            Assert.Contains("MemoryPeak", row.ValidationError, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(5, "TotalCpu")]
        [InlineData(6, "Duration")]
        public async Task Reader_rejects_invalid_timespans_and_does_not_guess_numeric_time_units(int ordinal, string column)
        {
            object[] invalidValues =
            [
                DBNull.Value, TimeSpan.FromTicks(-1), "-00:00:01", "not-a-timespan", "1", "0", "00:00",
                "99999999999999.00:00:00", double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, 0d, 123L, false, At(0)
            ];
            foreach (var invalid in invalidValues)
            {
                var values = ValidRow();
                values[ordinal] = invalid;
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Null(ordinal == 5 ? row.CpuSeconds : row.DurationSeconds);
                Assert.Equal(ordinal == 5 ? 1.75d : 8.125d, ordinal == 5 ? row.DurationSeconds : row.CpuSeconds);
                Assert.Equal(8589934593L, row.MemoryPeakBytes);
                Assert.Contains(column, row.ValidationError, StringComparison.Ordinal);
                Assert.DoesNotContain(ordinal == 5 ? "Duration" : "TotalCpu", row.ValidationError, StringComparison.Ordinal);
                Assert.DoesNotContain("MemoryPeak", row.ValidationError, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Reader_converts_integral_memory_values_without_rounding_or_int64_overflow()
        {
            (object Value, long Expected)[] cases =
            [
                ((sbyte)0, 0), ((byte)0, 0), ((short)1, 1), ((ushort)2, 2),
                (3, 3), (4U, 4), (0L, 0), (0d, 0), (0f, 0), (0m, 0), ("0", 0),
                (long.MaxValue, long.MaxValue), ((ulong)long.MaxValue, long.MaxValue),
                ((decimal)long.MaxValue, long.MaxValue), (1024d, 1024), (2048f, 2048),
                (Math.BitDecrement(9223372036854775808d), 9223372036854774784L)
            ];
            foreach (var item in cases)
            {
                var values = ValidRow();
                values[7] = item.Value;
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Equal(item.Expected, row.MemoryPeakBytes);
                Assert.Null(row.ValidationError);
            }
        }

        [Fact]
        public async Task Reader_rejects_negative_nonfinite_fractional_and_out_of_range_memory_without_losing_other_metrics()
        {
            object[] invalidValues =
            [
                DBNull.Value, -1L, -1, -1d, -1m, ulong.MaxValue, decimal.MaxValue,
                double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.NaN, float.PositiveInfinity,
                (double)long.MaxValue, 9223372036854775808m, 1.5d, 1.5f, 1.5m, double.Epsilon,
                "1.5", "1,000", "9223372036854775808", "-1", "not-a-long", false, TimeSpan.Zero, At(0)
            ];
            foreach (var invalid in invalidValues)
            {
                var values = ValidRow();
                values[7] = invalid;
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Null(row.MemoryPeakBytes);
                Assert.Equal(8.125d, row.CpuSeconds);
                Assert.Equal(1.75d, row.DurationSeconds);
                Assert.Contains("MemoryPeak", row.ValidationError, StringComparison.Ordinal);
                Assert.DoesNotContain("TotalCpu", row.ValidationError, StringComparison.Ordinal);
                Assert.DoesNotContain("Duration", row.ValidationError, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Reader_preserves_repeated_client_ids_server_ids_and_noncompleted_states_as_distinct_records()
        {
            var first = ValidRow();
            var failed = ValidRow();
            failed[1] = Guid.Parse("22d815af-77c2-40d3-8bcb-d8955b2e4951");
            failed[4] = "Failed";
            using var table = StatisticsTable(first, failed, first.ToArray());
            using var wire = new TrackingDataReader(table.CreateDataReader());

            var rows = await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(
                Query() with { ClientRequestIds = [RequestId, RequestId], MaxResults = 3 });

            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(RequestId, row.ClientRequestId));
            Assert.Equal("Failed", rows[1].State);
            Assert.NotEqual(rows[0].ServerActivityId, rows[1].ServerActivityId);
            Assert.Equal(rows[0], rows[2]);
        }

        [Fact]
        public async Task Reader_resolves_columns_by_name_not_position()
        {
            using var table = StatisticsTable(ValidRow());
            table.Columns["MemoryPeak"]!.SetOrdinal(0);
            table.Columns["Duration"]!.SetOrdinal(1);
            using var wire = new TrackingDataReader(table.CreateDataReader());

            var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Equal(8589934593L, row.MemoryPeakBytes);
            Assert.Equal(1.75d, row.DurationSeconds);
            Assert.Equal(8.125d, row.CpuSeconds);
            Assert.Equal(RequestId, row.ClientRequestId);
        }

        [Theory]
        [InlineData("ClientActivityId")]
        [InlineData("RootActivityId")]
        [InlineData("StartedOn")]
        [InlineData("LastUpdatedOn")]
        [InlineData("State")]
        [InlineData("TotalCpu")]
        [InlineData("Duration")]
        [InlineData("MemoryPeak")]
        public async Task Reader_rejects_missing_columns_even_in_an_empty_result(string column)
        {
            using var table = StatisticsTable();
            table.Columns.Remove(column);
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new KustoCommandStatisticsReader(factory).ReadAsync(Query()));

            Assert.Contains(column, error.Message, StringComparison.Ordinal);
            Assert.True(wire.Disposed);
            Assert.True(factory.Client.Disposed);
        }

        [Theory]
        [InlineData("ClientActivityId")]
        [InlineData("RootActivityId")]
        [InlineData("StartedOn")]
        [InlineData("LastUpdatedOn")]
        [InlineData("State")]
        public async Task Reader_rejects_invalid_required_column_types_without_requiring_a_row(string column)
        {
            using var table = StatisticsTable();
            table.Columns[column]!.DataType = typeof(bool);
            using var wire = new TrackingDataReader(table.CreateDataReader());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Contains(column, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Reader_rejects_extra_and_duplicate_columns_and_incomplete_rows()
        {
            using var extra = StatisticsTable();
            extra.Columns.Add("Text", typeof(string));
            using var extraWire = new TrackingDataReader(extra.CreateDataReader());
            await Assert.ThrowsAsync<InvalidDataException>(() => new KustoCommandStatisticsReader(new FakeFactory(extraWire)).ReadAsync(Query()));

            using var duplicate = StatisticsTable();
            using var duplicateWire = new TrackingDataReader(duplicate.CreateDataReader())
            {
                NameOverride = ordinal => ordinal == 1 ? "ClientActivityId" : ColumnNames[ordinal]
            };
            var duplicateError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(duplicateWire)).ReadAsync(Query()));
            Assert.Contains("duplicate", duplicateError.Message, StringComparison.Ordinal);

            using var incomplete = StatisticsTable(ValidRow());
            using var incompleteWire = new TrackingDataReader(incomplete.CreateDataReader()) { ValuesCountOverride = 7 };
            var incompleteError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(incompleteWire)).ReadAsync(Query()));
            Assert.Contains("incomplete", incompleteError.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Reader_rejects_malformed_required_values_and_out_of_scope_identity_or_time()
        {
            (int Ordinal, object Value)[] cases =
            [
                (0, DBNull.Value), (0, ""), (0, " "), (0, 1), (0, "unrequested"),
                (0, RequestId.ToUpperInvariant()), (1, DBNull.Value), (1, Guid.Empty), (1, "not-a-guid"), (1, 1),
                (2, DBNull.Value), (2, "not-a-date"), (2, "12:30:00"), (2, "01/01/2026"), (2, 0L),
                (2, At(-61)), (2, At(3600)), (3, DBNull.Value), (3, "not-a-date"), (3, At(-1)),
                (4, DBNull.Value), (4, ""), (4, " "), (4, 1)
            ];
            foreach (var item in cases)
            {
                var values = ValidRow();
                values[item.Ordinal] = item.Value;
                using var table = StatisticsTable(values);
                using var wire = new TrackingDataReader(table.CreateDataReader());

                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

                Assert.Contains(ColumnNames[item.Ordinal], error.Message, StringComparison.Ordinal);
                Assert.True(wire.Disposed);
            }
        }

        [Theory]
        [InlineData(1, 1, false)]
        [InlineData(1, 2, true)]
        [InlineData(1, 3, true)]
        [InlineData(2, 2, false)]
        [InlineData(2, 3, true)]
        public async Task Reader_exhausts_the_result_and_throws_instead_of_silently_truncating(int maxResults, int count, bool overflow)
        {
            using var table = StatisticsTable(Enumerable.Range(0, count).Select(_ => ValidRow()).ToArray());
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            var reader = new KustoCommandStatisticsReader(factory);
            var query = Query() with { MaxResults = maxResults };

            if (overflow)
            {
                await Assert.ThrowsAsync<CommandStatisticsResultTooLargeException>(() => reader.ReadAsync(query));
            }
            else
            {
                Assert.Equal(count, (await reader.ReadAsync(query)).Count);
            }

            Assert.EndsWith($"| take {maxResults + 1}", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.Equal(count, wire.RowsRead);
            Assert.True(wire.Exhausted);
            Assert.True(wire.Disposed);
            Assert.True(factory.Client.Disposed);
        }

        [Fact]
        public async Task Reader_counts_overflow_across_result_sets_without_deduplicating()
        {
            using var first = StatisticsTable(ValidRow());
            using var second = StatisticsTable(ValidRow());
            using var wire = new TrackingDataReader(new DataTableReader([first, second]));

            await Assert.ThrowsAsync<CommandStatisticsResultTooLargeException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query() with { MaxResults = 1 }));

            Assert.Equal(2, wire.RowsRead);
            Assert.True(wire.Exhausted);
        }

        [Fact]
        public async Task Reader_drains_documented_status_and_auxiliary_tables()
        {
            using var primary = StatisticsTable(ValidRow());
            using var status = StatusTable(4);
            using var properties = AuxiliaryTable(["TableId", "Key", "Value"], [0, "Visualization", "{}"]);
            using var legacyProperties = AuxiliaryTable(["Value"], ["{}"]);
            using var contents = AuxiliaryTable(["Ordinal", "Kind", "Name", "Id", "PrettyName"], [0, "QueryResult", "PrimaryResult", "", ""]);
            using var wire = new TrackingDataReader(new DataTableReader([primary, status, properties, legacyProperties, contents]));

            var row = Assert.Single(await new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Equal(8.125d, row.CpuSeconds);
            Assert.Equal(5, wire.RowsRead);
            Assert.True(wire.Exhausted);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Reader_rejects_trailing_partial_failure_status_even_after_result_overflow(int severity)
        {
            using var primary = StatisticsTable(ValidRow(), ValidRow());
            using var status = StatusTable(severity);
            using var wire = new TrackingDataReader(new DataTableReader([primary, status]));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query() with { MaxResults = 1 }));

            Assert.Contains("partial query failure", error.Message, StringComparison.Ordinal);
            Assert.Equal(3, wire.RowsRead);
            Assert.True(wire.Exhausted);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not-a-number")]
        [InlineData("-1")]
        [InlineData("2147483648")]
        public async Task Reader_rejects_malformed_status_instead_of_assuming_success(string? severity)
        {
            using var primary = StatisticsTable(ValidRow());
            using var status = StatusTable(severity is null ? DBNull.Value : severity);
            using var wire = new TrackingDataReader(new DataTableReader([primary, status]));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.Contains("Severity", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Reader_rejects_unknown_trailing_result_schemas()
        {
            using var primary = StatisticsTable(ValidRow());
            using var unexpected = AuxiliaryTable(["Unexpected"], ["not statistics"]);
            using var wire = new TrackingDataReader(new DataTableReader([primary, unexpected]));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(wire)).ReadAsync(Query()));

            Assert.True(wire.Disposed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Reader_propagates_late_stream_failures_including_after_the_overflow_sentinel(bool afterOverflow)
        {
            using var table = StatisticsTable(ValidRow(), ValidRow());
            var failure = new KustoServicePartialQueryFailureLowMemoryConditionException("Partial failure.", new IOException("broken stream"));
            using var wire = new TrackingDataReader(table.CreateDataReader())
            {
                BeforeRead = count =>
                {
                    if (count == (afterOverflow ? 3 : 2))
                    {
                        throw failure;
                    }
                }
            };
            var factory = new FakeFactory(wire);

            var error = await Assert.ThrowsAsync<KustoServicePartialQueryFailureLowMemoryConditionException>(() =>
                new KustoCommandStatisticsReader(factory).ReadAsync(Query() with { MaxResults = 1 }));

            Assert.Same(failure, error);
            Assert.True(wire.Disposed);
            Assert.True(factory.Client.Disposed);
        }

        [Fact]
        public async Task Reader_propagates_failures_from_advancing_results_and_materializing_values()
        {
            using var table = StatisticsTable(ValidRow());
            var failure = new IOException("Truncated response.");
            using var nextWire = new TrackingDataReader(table.CreateDataReader()) { BeforeNextResult = () => throw failure };
            var nextError = await Assert.ThrowsAsync<IOException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(nextWire)).ReadAsync(Query()));
            Assert.Same(failure, nextError);

            using var valuesWire = new TrackingDataReader(table.CreateDataReader()) { BeforeGetValues = () => throw failure };
            var valuesError = await Assert.ThrowsAsync<IOException>(() =>
                new KustoCommandStatisticsReader(new FakeFactory(valuesWire)).ReadAsync(Query()));
            Assert.Same(failure, valuesError);
        }

        [Fact]
        public async Task Reader_rejects_invalid_queries_before_creating_any_client()
        {
            var valid = Query();
            KustoCommandStatisticsQuery[] queries =
            [
                valid with { ClusterUri = null! },
                valid with { ClusterUri = new Uri("http://statistics.example.invalid") },
                valid with { ClusterUri = new Uri("relative", UriKind.Relative) },
                valid with { Database = null! },
                valid with { Database = " " },
                valid with { FromUtc = valid.ToUtc },
                valid with { FromUtc = valid.ToUtc.AddTicks(1) },
                valid with { FromUtc = valid.ToUtc.AddDays(-30).AddTicks(-1) },
                valid with { ClientRequestIds = null! },
                valid with { ClientRequestIds = [] },
                valid with { ClientRequestIds = [null!] },
                valid with { ClientRequestIds = [" "] },
                valid with { ClientRequestIds = Enumerable.Range(0, 201).Select(i => $"request-{i}").ToArray() },
                valid with { MaxResults = 0 },
                valid with { MaxResults = -1 },
                valid with { MaxResults = int.MaxValue }
            ];
            foreach (var query in queries)
            {
                using var table = StatisticsTable();
                using var wire = new TrackingDataReader(table.CreateDataReader());
                var factory = new FakeFactory(wire);

                await Assert.ThrowsAnyAsync<ArgumentException>(() => new KustoCommandStatisticsReader(factory).ReadAsync(query));

                Assert.Equal(0, factory.CreateCount);
                Assert.Null(factory.Client.CommandText);
            }
        }

        [Fact]
        public async Task Reader_accepts_exactly_200_ids_and_a_30_day_window_and_checks_null_arguments()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            var reader = new KustoCommandStatisticsReader(factory);
            var query = Query() with
            {
                FromUtc = At(3600).AddDays(-30),
                ClientRequestIds = Enumerable.Range(0, 200).Select(i => $"request-{i}").ToArray()
            };

            Assert.Empty(await reader.ReadAsync(query));
            Assert.Equal(1, factory.CreateCount);
            Assert.EndsWith("| take 1001", factory.Client.CommandText, StringComparison.Ordinal);
            Assert.Throws<ArgumentNullException>(() => new KustoCommandStatisticsReader(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => reader.ReadAsync(null!));
        }

        [Fact]
        public async Task Reader_does_not_create_a_client_when_already_cancelled()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new KustoCommandStatisticsReader(factory).ReadAsync(Query(), cancellation.Token));

            Assert.Equal(0, factory.CreateCount);
            Assert.Null(factory.Client.CommandText);
        }

        [Fact]
        public async Task Reader_passes_cancellation_to_the_client_and_preserves_it()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            using var cancellation = new CancellationTokenSource();
            factory.Client.OnExecute = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<IDataReader>(token);
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new KustoCommandStatisticsReader(factory).ReadAsync(Query(), cancellation.Token));

            Assert.True(factory.Client.CancellationToken.IsCancellationRequested);
            Assert.True(factory.Client.Disposed);
        }

        [Fact]
        public async Task Reader_does_not_dispatch_when_cancellation_arrives_during_client_creation()
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            using var cancellation = new CancellationTokenSource();
            var factory = new FakeFactory(wire) { OnCreate = cancellation.Cancel };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new KustoCommandStatisticsReader(factory).ReadAsync(Query(), cancellation.Token));

            Assert.Equal(1, factory.CreateCount);
            Assert.Null(factory.Client.CommandText);
            Assert.True(factory.Client.Disposed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Reader_honors_cancellation_during_row_reads_and_result_advancement(bool nextResult)
        {
            using var table = StatisticsTable(ValidRow());
            using var cancellation = new CancellationTokenSource();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            if (nextResult)
            {
                wire.AfterNextResult = cancellation.Cancel;
            }
            else
            {
                wire.AfterRead = _ => cancellation.Cancel();
            }

            var factory = new FakeFactory(wire);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new KustoCommandStatisticsReader(factory).ReadAsync(Query(), cancellation.Token));

            Assert.True(wire.Disposed);
            Assert.True(factory.Client.Disposed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Reader_propagates_client_timeouts_and_authorization_failures_without_success_shaped_fallbacks(bool timeout)
        {
            using var table = StatisticsTable();
            using var wire = new TrackingDataReader(table.CreateDataReader());
            var factory = new FakeFactory(wire);
            Exception failure = timeout ? new TimeoutException("Request deadline elapsed.") : new KustoRequestException("Forbidden.", new InvalidOperationException("authorization"));
            factory.Client.OnExecute = _ => Task.FromException<IDataReader>(failure);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => new KustoCommandStatisticsReader(factory).ReadAsync(Query()));

            Assert.Same(failure, error);
            Assert.True(factory.Client.Disposed);
        }

        private static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

        private static KustoCommandStatisticsQuery Query() => new(Cluster, "RecordedDb", At(-60), At(3600), [RequestId]);

        private static object[] ValidRow() =>
        [
            RequestId, ActivityId, At(0).UtcDateTime, At(5).UtcDateTime, "Completed",
            TimeSpan.FromMilliseconds(8125), TimeSpan.FromMilliseconds(1750), 8589934593L
        ];

        private static DataTable StatisticsTable(params object[][] rows) => AuxiliaryTable(ColumnNames, rows);

        private static DataTable StatusTable(object severity) => AuxiliaryTable(
            ["Timestamp", "Severity", "SeverityName", "StatusCode", "StatusDescription", "Count", "RequestId", "ActivityId", "SubActivityId", "ClientActivityId"],
            [At(0).UtcDateTime, severity, "Info", 0, "Query status.", 1, "statistics-request", ActivityId, Guid.Empty, "KoLite.Local.Performance;read"]);

        private static DataTable AuxiliaryTable(string[] columns, params object[][] rows)
        {
            var table = new DataTable();
            foreach (var column in columns)
            {
                table.Columns.Add(column, typeof(object));
            }

            foreach (var row in rows)
            {
                table.Rows.Add(row);
            }

            return table;
        }

        private sealed class FakeFactory : IKustoControlCommandClientFactory
        {
            public FakeClient Client { get; }
            public Uri? ClusterUri { get; private set; }
            public string? Database { get; private set; }
            public int CreateCount { get; private set; }
            public Action? OnCreate { get; init; }

            public FakeFactory(IDataReader reader) => Client = new FakeClient(reader);

            public IKustoControlCommandClient Create(KustoExecutionRequest request) =>
                throw new InvalidOperationException("Statistics must use the explicitly scoped CreateForDatabase path.");

            public KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request) =>
                throw new InvalidOperationException("Statistics must not construct an output execution request.");

            public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database)
            {
                CreateCount++;
                ClusterUri = clusterUri;
                Database = database;
                OnCreate?.Invoke();
                return Client;
            }
        }

        private sealed class FakeClient : IKustoControlCommandClient
        {
            private readonly IDataReader reader;
            public string? Database { get; private set; }
            public string? CommandText { get; private set; }
            public ClientRequestProperties? Properties { get; private set; }
            public CancellationToken CancellationToken { get; private set; }
            public bool Disposed { get; private set; }
            public Func<CancellationToken, Task<IDataReader>>? OnExecute { get; set; }

            public FakeClient(IDataReader reader) => this.reader = reader;

            public Task<IDataReader> ExecuteControlCommandAsync(
                string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken)
            {
                Database = database;
                CommandText = commandText;
                Properties = properties;
                CancellationToken = cancellationToken;
                return OnExecute is null ? Task.FromResult(reader) : OnExecute(cancellationToken);
            }

            public void Dispose() => Disposed = true;
        }

        private sealed class TrackingDataReader : IDataReader
        {
            private readonly IDataReader inner;
            private int readCount;
            public int RowsRead { get; private set; }
            public bool Exhausted { get; private set; }
            public bool Disposed { get; private set; }
            public Action<int>? BeforeRead { get; init; }
            public Action<bool>? AfterRead { get; set; }
            public Action? BeforeNextResult { get; init; }
            public Action? AfterNextResult { get; set; }
            public Action? BeforeGetValues { get; init; }
            public Func<int, string>? NameOverride { get; init; }
            public int? ValuesCountOverride { get; init; }

            public TrackingDataReader(IDataReader inner) => this.inner = inner;

            public bool Read()
            {
                BeforeRead?.Invoke(++readCount);
                var result = inner.Read();
                if (result)
                {
                    RowsRead++;
                }

                AfterRead?.Invoke(result);
                return result;
            }

            public bool NextResult()
            {
                BeforeNextResult?.Invoke();
                var result = inner.NextResult();
                Exhausted = !result;
                AfterNextResult?.Invoke();
                return result;
            }

            public int GetValues(object[] values)
            {
                BeforeGetValues?.Invoke();
                var count = inner.GetValues(values);
                return ValuesCountOverride ?? count;
            }

            public void Dispose()
            {
                Disposed = true;
                inner.Dispose();
            }

            public int Depth => inner.Depth;
            public bool IsClosed => inner.IsClosed;
            public int RecordsAffected => inner.RecordsAffected;
            public int FieldCount => inner.FieldCount;
            public object this[int i] => inner[i];
            public object this[string name] => inner[name];
            public void Close() => inner.Close();
            public DataTable? GetSchemaTable() => inner.GetSchemaTable();
            public bool GetBoolean(int i) => inner.GetBoolean(i);
            public byte GetByte(int i) => inner.GetByte(i);
            public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(i, fieldOffset, buffer, bufferOffset, length);
            public char GetChar(int i) => inner.GetChar(i);
            public long GetChars(int i, long fieldOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(i, fieldOffset, buffer, bufferOffset, length);
            public IDataReader GetData(int i) => inner.GetData(i);
            public string GetDataTypeName(int i) => inner.GetDataTypeName(i);
            public DateTime GetDateTime(int i) => inner.GetDateTime(i);
            public decimal GetDecimal(int i) => inner.GetDecimal(i);
            public double GetDouble(int i) => inner.GetDouble(i);
            public Type GetFieldType(int i) => inner.GetFieldType(i);
            public float GetFloat(int i) => inner.GetFloat(i);
            public Guid GetGuid(int i) => inner.GetGuid(i);
            public short GetInt16(int i) => inner.GetInt16(i);
            public int GetInt32(int i) => inner.GetInt32(i);
            public long GetInt64(int i) => inner.GetInt64(i);
            public string GetName(int i) => NameOverride?.Invoke(i) ?? inner.GetName(i);
            public int GetOrdinal(string name) => inner.GetOrdinal(name);
            public string GetString(int i) => inner.GetString(i);
            public object GetValue(int i) => inner.GetValue(i);
            public bool IsDBNull(int i) => inner.IsDBNull(i);
        }
    }
}
