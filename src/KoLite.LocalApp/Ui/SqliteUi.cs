using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Ui
{
    internal static class SqliteUi
    {
        public static void Add(this SqliteCommand command, string name, object? value) =>
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        public static DateTimeOffset ReadUtc(this SqliteDataReader reader, string name) =>
            ParseUtc(reader.GetString(reader.GetOrdinal(name)));

        public static DateTimeOffset? ReadNullableUtc(this SqliteDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : ParseUtc(reader.GetString(ordinal));
        }

        public static DateTimeOffset ParseUtc(string value) =>
            DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public static string FormatUtc(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }
}
