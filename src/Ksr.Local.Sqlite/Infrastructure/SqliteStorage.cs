// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Infrastructure
{
    internal static class SqliteStorage
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        // Same serializer behavior as JsonOptions but indented. Used only for human-facing
        // export output; storage and canonicalization stay on the compact JsonOptions.
        internal static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        internal static string Utc(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        internal static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        internal static DateTimeOffset ReadUtc(SqliteDataReader reader, string name) => ParseUtc(reader.GetString(reader.GetOrdinal(name)));

        internal static DateTimeOffset? ReadNullableUtc(SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : ParseUtc(reader.GetString(ordinal));
        }

        internal static string CanonicalJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
        }

        internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string text)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = text;
            return command;
        }

        internal static void Add(this SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
