// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace KoLite.LocalApp.Ui
{
    public sealed record ChartRangeLink(string Key, string Label);

    public static class ChartRangeOptions
    {
        public static IReadOnlyList<ChartRangeLink> Links { get; } =
        [
            new("1h", "1 hour"),
            new("1d", "1 day"),
            new("7d", "7 days"),
            new("30d", "30 days")
        ];

        public static string Normalize(string? range) => range switch
        {
            "1h" or "1d" or "7d" or "30d" => range,
            _ => "1d"
        };

        public static TimeSpan Parse(string range) => range switch
        {
            "1h" => TimeSpan.FromHours(1),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };
    }
}
