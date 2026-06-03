using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Infrastructure
{
    internal static class SqliteStorage
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

        internal static string Utc(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        internal static DateTimeOffset ReadUtc(SqliteDataReader reader, string name) => DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        internal static DateTimeOffset? ReadNullableUtc(SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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
