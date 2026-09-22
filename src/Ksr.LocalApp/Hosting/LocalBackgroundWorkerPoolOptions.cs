// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ksr.LocalApp
{
    public sealed record LocalBackgroundWorkerPoolOptions(
        bool Enabled,
        string EnabledSource,
        string Mode,
        int MaxConcurrency,
        string MaxConcurrencySource,
        TimeSpan IdleDelay,
        string IdleDelaySource,
        int MaxDispatchStartsPerCycle,
        string MaxDispatchStartsPerCycleSource,
        bool LogEveryPass)
    {
        public const string FixedMode = "Fixed";
        public const int Unbounded = int.MaxValue;
        public const int DefaultMaxConcurrency = Unbounded;
        public const int DefaultMaxDispatchStartsPerCycle = 100;
        public static TimeSpan DefaultIdleDelay { get; } = TimeSpan.FromMilliseconds(250);

        public bool MaxConcurrencyUnbounded => MaxConcurrency == Unbounded;
        public string MaxConcurrencyDisplay => MaxConcurrencyUnbounded ? "Unbounded" : MaxConcurrency.ToString(CultureInfo.InvariantCulture);

        public static LocalBackgroundWorkerPoolOptions From(IConfiguration configuration, LocalBackgroundSchedulerOptions schedulerOptions)
        {
            var configuredMode = configuration["Ksr:WorkerPool:Mode"];
            var mode = string.IsNullOrWhiteSpace(configuredMode) ? FixedMode : configuredMode.Trim();
            if (!string.Equals(mode, FixedMode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported Ksr:WorkerPool:Mode '{mode}'. Supported values: {FixedMode}.");
            }

            var maxConcurrency = ReadPositiveInt(
                configuration,
                "Ksr:WorkerPool:MaxConcurrency",
                "Ksr:Scheduler:WorkerConcurrency",
                DefaultMaxConcurrency,
                out var maxConcurrencySource);
            var idleDelay = ReadPositiveTimeSpan(
                configuration,
                "Ksr:WorkerPool:IdleDelay",
                DefaultIdleDelay,
                out var idleDelaySource);
            var maxDispatchStartsPerCycle = ReadPositiveInt(
                configuration,
                "Ksr:WorkerPool:MaxDispatchStartsPerCycle",
                "Ksr:Scheduler:MaxWorkerIterations",
                DefaultMaxDispatchStartsPerCycle,
                out var maxDispatchStartsPerCycleSource);

            return new LocalBackgroundWorkerPoolOptions(
                schedulerOptions.Enabled,
                "Ksr:Scheduler:Enabled",
                FixedMode,
                maxConcurrency,
                maxConcurrencySource,
                idleDelay,
                idleDelaySource,
                maxDispatchStartsPerCycle,
                maxDispatchStartsPerCycleSource,
                schedulerOptions.LogEveryPass);
        }

        private static int ReadPositiveInt(IConfiguration configuration, string primaryKey, string aliasKey, int defaultValue, out string source)
        {
            var configured = configuration[primaryKey];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                source = primaryKey;
                return ParsePositiveInt(primaryKey, configured);
            }

            configured = configuration[aliasKey];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                source = aliasKey;
                return ParsePositiveInt(aliasKey, configured);
            }

            source = "Default";
            return defaultValue;
        }

        private static int ParsePositiveInt(string key, string value)
        {
            var parsed = int.Parse(value, CultureInfo.InvariantCulture);
            if (parsed <= 0) throw new InvalidOperationException($"{key} must be greater than zero.");
            return parsed;
        }

        private static TimeSpan ReadPositiveTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue, out string source)
        {
            var configured = configuration[key];
            if (string.IsNullOrWhiteSpace(configured))
            {
                source = "Default";
                return defaultValue;
            }

            var parsed = TimeSpan.Parse(configured, CultureInfo.InvariantCulture);
            if (parsed <= TimeSpan.Zero) throw new InvalidOperationException($"{key} must be greater than zero.");
            source = key;
            return parsed;
        }
    }
}
