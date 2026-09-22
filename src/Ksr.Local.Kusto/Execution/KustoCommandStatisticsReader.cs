// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data;
using System.Globalization;
using System.Text;
using Kusto.Data.Common;
using Ksr.Local.Core.Performance;

namespace Ksr.Local.Kusto.Execution
{
    public sealed class KustoCommandStatisticsReader : IKustoCommandStatisticsReader
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(30);
        private static readonly string[] StatisticsColumns =
        [
            "ClientActivityId", "RootActivityId", "StartedOn", "LastUpdatedOn",
            "State", "TotalCpu", "Duration", "MemoryPeak"
        ];
        private static readonly string[] StatusColumns =
        [
            "Timestamp", "Severity", "SeverityName", "StatusCode", "StatusDescription",
            "Count", "RequestId", "ActivityId", "SubActivityId", "ClientActivityId"
        ];
        private static readonly string[] DateTimeFormats =
        [
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"
        ];

        private readonly IKustoControlCommandClientFactory clientFactory;

        public KustoCommandStatisticsReader(IKustoControlCommandClientFactory clientFactory) =>
            this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

        public async Task<IReadOnlyList<KustoCommandStatistics>> ReadAsync(
            KustoCommandStatisticsQuery query,
            CancellationToken cancellationToken = default)
        {
            ValidateQuery(query);
            var allowedRequestIds = new HashSet<string>(query.ClientRequestIds, StringComparer.Ordinal);
            var commandText = BuildCommand(query);
            cancellationToken.ThrowIfCancellationRequested();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CommandTimeout);
            using var client = clientFactory.CreateForDatabase(query.ClusterUri, query.Database);
            var properties = new ClientRequestProperties { ClientRequestId = $"Ksr.Local.Performance;{Guid.NewGuid():N}" };
            properties.SetOption(ClientRequestProperties.OptionServerTimeout, CommandTimeout);
            properties.SetOption(ClientRequestProperties.OptionDeferPartialQueryFailures, false);
            deadline.Token.ThrowIfCancellationRequested();

            using var reader = await client.ExecuteControlCommandAsync(
                query.Database, commandText, properties, deadline.Token).ConfigureAwait(false);
            return ReadResponse(reader, query, allowedRequestIds, deadline.Token);
        }

        private static void ValidateQuery(KustoCommandStatisticsQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(query.ClusterUri);
            ArgumentNullException.ThrowIfNull(query.ClientRequestIds);
            if (!query.ClusterUri.IsAbsoluteUri || query.ClusterUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Kusto statistics require an absolute HTTPS cluster URI.", nameof(query));
            }

            if (string.IsNullOrWhiteSpace(query.Database))
            {
                throw new ArgumentException("Database is required for Kusto statistics.", nameof(query));
            }

            if (query.FromUtc >= query.ToUtc || query.ToUtc - query.FromUtc > MaximumWindow)
            {
                throw new ArgumentException("Kusto statistics require an increasing time window of at most 30 days.", nameof(query));
            }

            if (query.ClientRequestIds.Count is < 1 or > 200 || query.ClientRequestIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Kusto statistics require between 1 and 200 nonempty client request IDs.", nameof(query));
            }

            if (query.MaxResults is < 1 or int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(query), "MaxResults must be positive and leave room for an overflow sentinel.");
            }
        }

        private static string BuildCommand(KustoCommandStatisticsQuery query)
        {
            var requestIds = string.Join(", ", query.ClientRequestIds.Select(KustoString));
            return string.Create(CultureInfo.InvariantCulture, $"""
                .show commands-and-queries
                | where Database == {KustoString(query.Database)}
                    and CommandType == "TableSetOrAppend"
                    and StartedOn >= datetime({FormatUtc(query.FromUtc)})
                    and StartedOn < datetime({FormatUtc(query.ToUtc)})
                    and ClientActivityId in ({requestIds})
                | project ClientActivityId,
                          RootActivityId,
                          StartedOn,
                          LastUpdatedOn,
                          State,
                          TotalCpu,
                          Duration,
                          MemoryPeak
                | take {query.MaxResults + 1}
                """);
        }

        private static string FormatUtc(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

        private static string KustoString(string value)
        {
            var literal = new StringBuilder().Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': literal.Append("\\\\"); break;
                    case '"': literal.Append("\\\""); break;
                    case '\r': literal.Append("\\r"); break;
                    case '\n': literal.Append("\\n"); break;
                    case '\t': literal.Append("\\t"); break;
                    default:
                        if (character < ' ' || character > '~')
                        {
                            literal.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            literal.Append(character);
                        }

                        break;
                }
            }

            return literal.Append('"').ToString();
        }

        private static IReadOnlyList<KustoCommandStatistics> ReadResponse(
            IDataReader reader,
            KustoCommandStatisticsQuery query,
            HashSet<string> allowedRequestIds,
            CancellationToken cancellationToken)
        {
            var results = new List<KustoCommandStatistics>();
            var overflow = false;
            var partialFailure = false;
            var firstResult = true;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var columns = ReadSchema(reader);
                var values = new object[reader.FieldCount];
                if (firstResult || HasColumns(columns, StatisticsColumns))
                {
                    ValidateStatisticsSchema(reader, columns);
                    while (ReadRow(reader, values, cancellationToken))
                    {
                        var statistics = ReadStatistics(values, columns, query, allowedRequestIds);
                        if (results.Count < query.MaxResults)
                        {
                            results.Add(statistics);
                        }
                        else
                        {
                            overflow = true;
                        }
                    }
                }
                else if (HasColumns(columns, StatusColumns))
                {
                    while (ReadRow(reader, values, cancellationToken))
                    {
                        var severity = ReadNonNegativeInt64(values[columns["Severity"]]);
                        if (severity is null or > int.MaxValue)
                        {
                            throw new InvalidDataException("Kusto command statistics returned an invalid QueryStatus Severity.");
                        }

                        partialFailure |= severity <= 2;
                    }
                }
                else if (HasColumns(columns, ["Value"])
                    || HasColumns(columns, ["TableId", "Key", "Value"])
                    || HasColumns(columns, ["Ordinal", "Kind", "Name", "Id", "PrettyName"]))
                {
                    while (ReadRow(reader, values, cancellationToken))
                    {
                    }
                }
                else
                {
                    throw new InvalidDataException("Kusto command statistics returned an unexpected result schema.");
                }

                firstResult = false;
            }
            while (NextResult(reader, cancellationToken));

            if (partialFailure)
            {
                throw new InvalidDataException("Kusto command statistics reported a partial query failure in QueryStatus; no statistics were accepted.");
            }

            if (overflow)
            {
                throw new CommandStatisticsResultTooLargeException(
                    $"Kusto command statistics exceeded MaxResults ({query.MaxResults}); split the request batch and retry.");
            }

            return results;
        }

        private static Dictionary<string, int> ReadSchema(IDataReader reader)
        {
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                var name = reader.GetName(ordinal);
                if (string.IsNullOrWhiteSpace(name) || !columns.TryAdd(name, ordinal))
                {
                    throw new InvalidDataException("Kusto command statistics returned an empty or duplicate column name.");
                }
            }

            return columns;
        }

        private static bool HasColumns(IReadOnlyDictionary<string, int> columns, string[] expected) =>
            columns.Count == expected.Length && expected.All(columns.ContainsKey);

        private static void ValidateStatisticsSchema(IDataReader reader, IReadOnlyDictionary<string, int> columns)
        {
            foreach (var name in StatisticsColumns)
            {
                if (!columns.TryGetValue(name, out var ordinal))
                {
                    throw new InvalidDataException($"Kusto command statistics is missing required column '{name}'.");
                }

                var type = reader.GetFieldType(ordinal);
                var compatible = type == typeof(object) || (name switch
                {
                    "ClientActivityId" or "State" => type == typeof(string),
                    "RootActivityId" => type == typeof(Guid) || type == typeof(string),
                    "StartedOn" or "LastUpdatedOn" => type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(string),
                    _ => true
                });
                if (!compatible)
                {
                    throw new InvalidDataException($"Kusto command statistics column '{name}' has an invalid data type.");
                }
            }

            if (columns.Count != StatisticsColumns.Length)
            {
                throw new InvalidDataException("Kusto command statistics returned unexpected columns.");
            }
        }

        private static bool ReadRow(IDataReader reader, object[] values, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasRow = reader.Read();
            if (hasRow && reader.GetValues(values) != values.Length)
            {
                throw new InvalidDataException("Kusto command statistics returned a row with an incomplete data shape.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return hasRow;
        }

        private static bool NextResult(IDataReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasResult = reader.NextResult();
            cancellationToken.ThrowIfCancellationRequested();
            return hasResult;
        }

        private static KustoCommandStatistics ReadStatistics(
            object[] values,
            IReadOnlyDictionary<string, int> columns,
            KustoCommandStatisticsQuery query,
            HashSet<string> allowedRequestIds)
        {
            var requestId = ReadRequiredString(values[columns["ClientActivityId"]], "ClientActivityId");
            if (!allowedRequestIds.Contains(requestId))
            {
                throw new InvalidDataException("Kusto command statistics returned a ClientActivityId outside the requested identities.");
            }

            var activityValue = values[columns["RootActivityId"]];
            var activityId = activityValue is Guid guid ? guid
                : activityValue is string text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
            if (activityId == Guid.Empty)
            {
                throw new InvalidDataException("Kusto command statistics returned a missing or invalid RootActivityId.");
            }

            var started = ReadUtc(values[columns["StartedOn"]], "StartedOn");
            var completed = ReadUtc(values[columns["LastUpdatedOn"]], "LastUpdatedOn");
            if (started < query.FromUtc || started >= query.ToUtc)
            {
                throw new InvalidDataException("Kusto command statistics returned StartedOn outside the requested time window.");
            }

            if (completed < started)
            {
                throw new InvalidDataException("Kusto command statistics returned LastUpdatedOn before StartedOn.");
            }

            var state = ReadRequiredString(values[columns["State"]], "State");
            var errors = new List<string>();
            var cpu = ReadSeconds(values[columns["TotalCpu"]], "TotalCpu", errors);
            var duration = ReadSeconds(values[columns["Duration"]], "Duration", errors);
            var memory = ReadNonNegativeInt64(values[columns["MemoryPeak"]]);
            if (memory is null)
            {
                errors.Add("MemoryPeak is missing or invalid; expected nonnegative Int64 bytes.");
            }

            return new KustoCommandStatistics(
                requestId, activityId, started, completed, state, cpu, duration, memory,
                errors.Count == 0 ? null : string.Join(" ", errors));
        }

        private static string ReadRequiredString(object? value, string column) =>
            value is string text && !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new InvalidDataException($"Kusto command statistics returned a missing or invalid {column}.");

        private static DateTimeOffset ReadUtc(object? value, string column)
        {
            if (value is DateTimeOffset offset)
            {
                return offset.ToUniversalTime();
            }

            if (value is DateTime dateTime)
            {
                return new DateTimeOffset(dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime).ToUniversalTime();
            }

            if (value is string text && DateTimeOffset.TryParseExact(
                text, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }

            throw new InvalidDataException($"Kusto command statistics returned a missing or invalid {column} UTC timestamp.");
        }

        private static double? ReadSeconds(object? value, string column, List<string> errors)
        {
            // The .NET "c" parser also accepts bare day counts; Kusto reports explicit hh:mm:ss units.
            TimeSpan? duration = value switch
            {
                TimeSpan timeSpan => timeSpan,
                string text when text.Count(character => character == ':') == 2
                    && TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
            if (duration is { } valid && valid >= TimeSpan.Zero && double.IsFinite(valid.TotalSeconds))
            {
                return valid.TotalSeconds;
            }

            errors.Add($"{column} is missing or invalid; expected a nonnegative timespan.");
            return null;
        }

        private static long? ReadNonNegativeInt64(object? value)
        {
            long? number = value switch
            {
                long integer => integer,
                int integer => integer,
                short integer => integer,
                sbyte integer => integer,
                byte integer => integer,
                ushort integer => integer,
                uint integer => integer,
                ulong integer when integer <= long.MaxValue => (long)integer,
                decimal real when real >= 0 && real <= long.MaxValue && real == decimal.Truncate(real) => (long)real,
                double real when IsNonNegativeInt64(real) => (long)real,
                float real when IsNonNegativeInt64(real) => (long)real,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
            return number is >= 0 ? number : null;
        }

        // long.MaxValue rounds up to 2^63 as a double, so that upper bound must be exclusive.
        private static bool IsNonNegativeInt64(double value) =>
            double.IsFinite(value) && value >= 0 && value < 9223372036854775808d && value == Math.Truncate(value);
    }
}
