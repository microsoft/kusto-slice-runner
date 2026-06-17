using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Kusto.Execution
{
    public sealed record KustoExecutionRequest(
        Uri ClusterUri,
        string Database,
        string FunctionName,
        string OutputTable,
        SliceRange Slice,
        string SliceKey,
        string OperationId,
        string IdempotencyKey,
        string IngestByTag,
        string CommandText,
        TimeSpan Timeout)
    {
        public string ClientRequestId => $"KoLite.Local.Output;{OperationId}";
        public string OutputReference => $"ko-lite:{SliceKey}";
    }

    public sealed record KustoExecutionResult(bool Succeeded, string? OutputReference, IReadOnlyDictionary<string, string> Metadata, KustoExecutionError? Error = null);
    public sealed record KustoExecutionError(string Code, string Message, bool IsRetryable);
    public interface IKustoRequestBuilder { KustoExecutionRequest Build(JobDefinition job, SliceRange slice); }
    public interface IKustoExecutor { Task<KustoExecutionResult> ExecuteAsync(KustoExecutionRequest request, CancellationToken cancellationToken = default); }

    public sealed partial class KustoRequestBuilder : IKustoRequestBuilder
    {
        public KustoExecutionRequest Build(JobDefinition job, SliceRange slice)
        {
            if (!Uri.TryCreate(job.Target.ClusterUri, UriKind.Absolute, out var clusterUri) || clusterUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Kusto cluster URI must be an absolute https URI.");
            ValidateIdentifier(job.Target.Database, nameof(job.Target.Database)); ValidateIdentifier(job.FunctionName, nameof(job.FunctionName)); ValidateIdentifier(job.OutputTable, nameof(job.OutputTable));
            if (!StringComparer.Ordinal.Equals(job.Id, slice.JobId)) throw new InvalidOperationException("Slice job id must match the job id.");

            var sliceKey = slice.ToKey().Value;
            var idempotencyKey = $"ko-lite:{sliceKey}";
            var ingestByTag = $"ingest-by:{idempotencyKey}";
            var ingestIfNotExists = KustoString(JsonSerializer.Serialize(new[] { idempotencyKey }));
            var tags = KustoString(JsonSerializer.Serialize(new[] { ingestByTag }));
            var commandText = string.Create(CultureInfo.InvariantCulture, $$"""
                .set-or-append {{job.OutputTable}} with (ingestIfNotExists = {{ingestIfNotExists}}, tags = {{tags}}) <|
                {{job.FunctionName}}({{BuildFunctionArguments(job, slice)}})
                """);

            return new KustoExecutionRequest(
                clusterUri,
                job.Target.Database,
                job.FunctionName,
                job.OutputTable,
                slice,
                sliceKey,
                $"output|{sliceKey}",
                idempotencyKey,
                ingestByTag,
                commandText,
                job.QueryTimeout);
        }

        private static string BuildFunctionArguments(JobDefinition job, SliceRange slice)
        {
            var start = FormatDateTime(slice.StartUtc);
            var end = FormatDateTime(slice.EndUtc);
            if (job.JobSettings is not { } settings || !HasNonEmptyJobSettings(settings))
            {
                return $"datetime({start}), datetime({end})";
            }

            return $"datetime({start}), datetime({end}), dynamic({settings.GetRawText()})";
        }

        private static bool HasNonEmptyJobSettings(JsonElement settings) =>
            settings.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.Object => settings.EnumerateObject().Any(),
                JsonValueKind.Array => settings.EnumerateArray().Any(),
                JsonValueKind.String => !string.IsNullOrEmpty(settings.GetString()),
                _ => true
            };

        private static string FormatDateTime(DateTimeOffset value) =>
            value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

        private static string KustoString(string value)
        {
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            return "\"" + escaped + "\"";
        }

        private static void ValidateIdentifier(string value, string field) { if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value)) throw new InvalidOperationException($"{field} must be a safe Kusto identifier."); }
        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)] private static partial Regex IdentifierRegex();
    }

    public sealed class KustoLocalSliceOutputExecutor : ILocalSliceOutputExecutor
    {
        private readonly IKustoRequestBuilder requestBuilder;
        private readonly IKustoExecutor executor;

        public KustoLocalSliceOutputExecutor(IKustoRequestBuilder requestBuilder, IKustoExecutor executor)
        {
            this.requestBuilder = requestBuilder;
            this.executor = executor;
        }

        public async Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
        {
            KustoExecutionRequest request;
            try
            {
                request = requestBuilder.Build(job, slice);
            }
            catch (InvalidOperationException ex)
            {
                return LocalSliceOutputResult.Failure("KustoRequestInvalid", ex.Message, isRetryable: false);
            }

            KustoExecutionResult result;
            try
            {
                result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return LocalSliceOutputResult.Failure("LiveKustoExecutionUnavailable", ex.Message, isRetryable: false);
            }
            catch (NotSupportedException ex)
            {
                return LocalSliceOutputResult.Failure("LiveKustoExecutionUnavailable", ex.Message, isRetryable: false);
            }

            if (result.Succeeded) return LocalSliceOutputResult.Success(result.OutputReference);
            var error = result.Error ?? KustoErrorClassifier.Classify("KustoExecutionFailed", "Kusto execution failed without a detailed error.");
            return LocalSliceOutputResult.Failure(error.Code, error.Message, error.IsRetryable);
        }
    }

    public sealed class KustoSdkExecutor : IKustoExecutor
    {
        private readonly IKustoControlCommandClientFactory clientFactory;

        public KustoSdkExecutor(IKustoControlCommandClientFactory clientFactory)
        {
            this.clientFactory = clientFactory;
        }

        public async Task<KustoExecutionResult> ExecuteAsync(KustoExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);

            try
            {
                using var client = clientFactory.Create(request);
                var properties = new ClientRequestProperties { ClientRequestId = request.ClientRequestId };
                properties.SetOption(ClientRequestProperties.OptionServerTimeout, request.Timeout);

                using var reader = await client.ExecuteControlCommandAsync(
                    request.Database,
                    request.CommandText,
                    properties,
                    cancellationToken).ConfigureAwait(false);
                Consume(reader);

                return Success(request, duplicateSuppressed: false);
            }
            catch (KustoRequestException ex) when (KustoDuplicateIngestByFailureDetector.IsDuplicateIngestByFailure(ex, request.IdempotencyKey))
            {
                return Success(request, duplicateSuppressed: true);
            }
            catch (KustoServiceException ex) when (KustoDuplicateIngestByFailureDetector.IsDuplicateIngestByFailure(ex, request.IdempotencyKey))
            {
                return Success(request, duplicateSuppressed: true);
            }
            catch (KustoRequestException ex)
            {
                return Failure(ex);
            }
            catch (KustoServiceException ex)
            {
                return Failure(ex);
            }
        }

        private static KustoExecutionResult Success(KustoExecutionRequest request, bool duplicateSuppressed) =>
            new(true, request.OutputReference, new Dictionary<string, string>
            {
                ["clientRequestId"] = request.ClientRequestId,
                ["idempotencyKey"] = request.IdempotencyKey,
                ["duplicateSuppressed"] = duplicateSuppressed ? "true" : "false"
            });

        private static KustoExecutionResult Failure(KustoException exception)
        {
            var error = KustoErrorClassifier.Classify(exception.GetType().Name, exception.Message);
            return new KustoExecutionResult(false, null, new Dictionary<string, string>(), error);
        }

        private static void ValidateRequest(KustoExecutionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.OutputTable)) throw new ArgumentException("OutputTable is required before executing Kusto output.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.SliceKey)) throw new ArgumentException("SliceKey is required before executing Kusto output.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.OperationId)) throw new ArgumentException("OperationId is required before executing Kusto output.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("IdempotencyKey is required before executing Kusto output.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.IngestByTag)) throw new ArgumentException("IngestByTag is required before executing Kusto output.", nameof(request));
        }

        private static void Consume(IDataReader reader)
        {
            while (reader.Read())
            {
            }
        }
    }

    internal static class KustoDuplicateIngestByFailureDetector
    {
        public static bool IsDuplicateIngestByFailure(KustoException exception, string idempotencyKey)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current.Message.Contains(idempotencyKey, StringComparison.Ordinal)
                    && (current.Message.Contains("ingest", StringComparison.OrdinalIgnoreCase)
                        || current.Message.Contains("ingest-by", StringComparison.OrdinalIgnoreCase)
                        || current.Message.Contains("ingestIfNotExists", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static class KustoErrorClassifier { public static KustoExecutionError Classify(string code, string message) { var text = $"{code} {message}"; var retryable = new[] { "timeout", "throttl", "temporar", "transient", "too many requests", "service unavailable" }.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)); return new KustoExecutionError(code, message, retryable); } }
}
