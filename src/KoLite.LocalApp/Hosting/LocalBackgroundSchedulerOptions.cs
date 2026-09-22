// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp
{
    public sealed record LocalBackgroundSchedulerOptions(bool Enabled, TimeSpan TickInterval, bool LogEveryPass)
    {
        public static TimeSpan DefaultTickInterval { get; } = TimeSpan.FromSeconds(10);

        public static LocalBackgroundSchedulerOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:Scheduler:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);
            var configuredTickInterval = configuration["KoLite:Scheduler:TickInterval"];
            var tickInterval = string.IsNullOrWhiteSpace(configuredTickInterval)
                ? DefaultTickInterval
                : TimeSpan.Parse(configuredTickInterval, CultureInfo.InvariantCulture);
            if (tickInterval <= TimeSpan.Zero) throw new InvalidOperationException("KoLite:Scheduler:TickInterval must be greater than zero.");
            var logEveryPassText = configuration["KoLite:Scheduler:LogEveryPass"];
            var logEveryPass = !string.IsNullOrWhiteSpace(logEveryPassText) && bool.Parse(logEveryPassText);
            return new LocalBackgroundSchedulerOptions(enabled, tickInterval, logEveryPass);
        }
    }
}
