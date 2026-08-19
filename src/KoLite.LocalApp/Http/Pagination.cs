using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KoLite.LocalApp.Application;

namespace KoLite.LocalApp.Http
{
    public sealed record PageEnvelope<T>(IReadOnlyList<T> Items, string? NextCursor);

    internal sealed record PageAnchor(DateTimeOffset TimestampUtc, string Id);

    internal static class Pagination
    {
        public const int DefaultLimit = 100;
        public const int MaximumLimit = 1000;

        public static int ParseLimit(string? raw, int defaultLimit = DefaultLimit, int maximum = MaximumLimit)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultLimit;
            }

            if (!int.TryParse(raw, out var value) || value < 1 || value > maximum)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status400BadRequest,
                    "invalid-limit",
                    "The page limit is invalid.",
                    $"'limit' must be an integer from 1 through {maximum}.");
            }

            return value;
        }

        public static string Fingerprint(params (string Name, string? Value)[] filters)
        {
            var canonical = string.Join(
                "\n",
                filters.OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => item.Name + "=" + (item.Value ?? string.Empty)));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        public static string Encode(string endpoint, string fingerprint, PageAnchor anchor)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(
                1,
                endpoint,
                fingerprint,
                anchor.TimestampUtc.ToUniversalTime(),
                anchor.Id));
            return Base64UrlEncode(payload);
        }

        public static PageAnchor? Decode(string? cursor, string endpoint, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return null;
            }

            CursorPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CursorPayload>(Base64UrlDecode(cursor));
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw InvalidCursor();
            }

            if (payload is null
                || payload.Version != 1
                || !StringComparer.Ordinal.Equals(payload.Endpoint, endpoint)
                || !StringComparer.Ordinal.Equals(payload.Fingerprint, fingerprint)
                || string.IsNullOrWhiteSpace(payload.Id))
            {
                throw InvalidCursor();
            }

            return new PageAnchor(payload.TimestampUtc.ToUniversalTime(), payload.Id);
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            return Convert.FromBase64String(normalized);
        }

        private static ApplicationProblemException InvalidCursor()
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-cursor",
                "The pagination cursor is invalid.",
                "The cursor is malformed, expired, or belongs to a different endpoint or filter set.");
        }

        private sealed record CursorPayload(
            int Version,
            string Endpoint,
            string Fingerprint,
            DateTimeOffset TimestampUtc,
            string Id);
    }
}
