// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp.FailureAnalysis
{
    // Configuration for the "Analyze failures with Copilot" feature. Kusto Slice Runner invokes GitHub Copilot
    // CLI in non-interactive, no-tools mode and reads its final markdown response from stdout.
    public sealed record CopilotAnalysisOptions
    {
        public const string DefaultExecutable = "copilot";
        public const string DefaultModel = "auto";

        public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(120);

        public bool Enabled { get; init; } = true;
        public string Executable { get; init; } = DefaultExecutable;
        public string Model { get; init; } = DefaultModel;
        public TimeSpan Timeout { get; init; } = DefaultTimeout;

        public static CopilotAnalysisOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:CopilotAnalysis:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);

            var executableText = configuration["KoLite:CopilotAnalysis:Executable"];
            var executable = string.IsNullOrWhiteSpace(executableText) ? DefaultExecutable : executableText.Trim();

            var modelText = configuration["KoLite:CopilotAnalysis:Model"];
            var model = string.IsNullOrWhiteSpace(modelText) ? DefaultModel : modelText.Trim();

            var timeout = ReadPositiveSeconds(configuration, "KoLite:CopilotAnalysis:TimeoutSeconds", DefaultTimeout);

            return new CopilotAnalysisOptions
            {
                Enabled = enabled,
                Executable = executable,
                Model = model,
                Timeout = timeout
            };
        }

        private static TimeSpan ReadPositiveSeconds(IConfiguration configuration, string key, TimeSpan defaultValue)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text)) return defaultValue;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                throw new InvalidOperationException($"{key} must be a positive number of seconds.");
            }

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
