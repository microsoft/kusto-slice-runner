// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.Local.Core.Schedules
{
    public static class ScheduleTags
    {
        public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> rawTags)
        {
            ArgumentNullException.ThrowIfNull(rawTags);

            var normalizedTags = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawTag in rawTags)
            {
                if (TryNormalize(rawTag, out var normalized) && seen.Add(normalized))
                {
                    normalizedTags.Add(normalized);
                }
            }

            return normalizedTags;
        }

        public static bool TryNormalize(string? rawTag, out string normalized)
        {
            normalized = string.Empty;
            if (rawTag is null)
            {
                return false;
            }

            var trimmed = rawTag.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            normalized = trimmed.ToLowerInvariant();
            return true;
        }
    }
}
