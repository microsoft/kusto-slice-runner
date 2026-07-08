using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace KoLite.LocalApp.FailureAnalysis
{
    // Configuration for the "Analyze failures with Copilot" feature. Inference runs against an
    // OpenAI-compatible endpoint (GitHub Models by default), so nothing is deployed or self-hosted -
    // KO Lite only sends already secret-sanitized failure evidence and reads back a markdown analysis.
    //
    // Authentication is automatic: the token is obtained on demand from the operator's GitHub CLI
    // sign-in (gh auth token) via ICopilotAnalysisTokenProvider, so no personal access token has to be
    // created or stored. The feature is gated only by Enabled; when disabled the runner returns a
    // friendly message instead of calling out. Endpoint and Model are fully overridable so the same code
    // can target any OpenAI-compatible gateway (including one that serves a different/stronger model)
    // without a code change.
    public sealed record CopilotAnalysisOptions
    {
        public const string DefaultEndpoint = "https://models.github.ai/inference";

        // GitHub Models hosts OpenAI/Llama/Mistral/Phi (not Anthropic), so this defaults to a strong
        // OpenAI model available there. Override KoLite:CopilotAnalysis:Model to target another model,
        // or point Endpoint at a gateway that serves your preferred model.
        public const string DefaultModel = "openai/gpt-4.1";

        public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(120);

        public bool Enabled { get; init; } = true;
        public Uri Endpoint { get; init; } = new(DefaultEndpoint);
        public string Model { get; init; } = DefaultModel;
        public TimeSpan Timeout { get; init; } = DefaultTimeout;
        public int? MaxOutputTokens { get; init; }

        public static CopilotAnalysisOptions From(IConfiguration configuration)
        {
            var enabledText = configuration["KoLite:CopilotAnalysis:Enabled"];
            var enabled = string.IsNullOrWhiteSpace(enabledText) || bool.Parse(enabledText);

            var endpointText = configuration["KoLite:CopilotAnalysis:Endpoint"];
            var endpoint = string.IsNullOrWhiteSpace(endpointText) ? new Uri(DefaultEndpoint) : new Uri(endpointText);

            var modelText = configuration["KoLite:CopilotAnalysis:Model"];
            var model = string.IsNullOrWhiteSpace(modelText) ? DefaultModel : modelText.Trim();

            var timeout = ReadPositiveSeconds(configuration, "KoLite:CopilotAnalysis:TimeoutSeconds", DefaultTimeout);
            var maxOutputTokens = ReadOptionalPositiveInt(configuration, "KoLite:CopilotAnalysis:MaxOutputTokens");

            return new CopilotAnalysisOptions
            {
                Enabled = enabled,
                Endpoint = endpoint,
                Model = model,
                Timeout = timeout,
                MaxOutputTokens = maxOutputTokens
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

        private static int? ReadOptionalPositiveInt(IConfiguration configuration, string key)
        {
            var text = configuration[key];
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new InvalidOperationException($"{key} must be a positive integer.");
            }

            return value;
        }
    }
}
