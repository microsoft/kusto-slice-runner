using System.Globalization;
using KoLite.LocalApp.Application;

namespace KoLite.LocalApp.Http
{
    internal static class UtcInput
    {
        public static DateTimeOffset? Optional(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Required(value, fieldName);
        }

        public static DateTimeOffset Required(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value) || !HasExplicitOffset(value))
            {
                throw Invalid(fieldName);
            }

            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                throw Invalid(fieldName);
            }

            return parsed.ToUniversalTime();
        }

        private static bool HasExplicitOffset(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
            {
                return true;
            }

            var timeSeparator = trimmed.IndexOf('T');
            if (timeSeparator < 0)
            {
                return false;
            }

            return trimmed.LastIndexOf('+') > timeSeparator || trimmed.LastIndexOf('-') > timeSeparator;
        }

        private static ApplicationProblemException Invalid(string fieldName)
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-utc-instant",
                "A UTC instant is invalid.",
                $"'{fieldName}' must be an ISO-8601 timestamp with an explicit UTC offset, for example '2026-01-01T00:00:00Z'.");
        }
    }
}
