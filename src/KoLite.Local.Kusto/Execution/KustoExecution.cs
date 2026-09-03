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
        string ExecutionKey,
        int? ChunkId,
        int? TotalChunks,
        string OperationId,
        string IdempotencyKey,
        string IngestByTag,
        string CommandText,
        TimeSpan Timeout)
    {
        public string ClientRequestId => $"KoLite.Local.Output;{OperationId}";
        public string OutputReference => $"ko-lite:{ExecutionKey}";
    }

    public sealed record KustoExecutionResult(bool Succeeded, string? OutputReference, IReadOnlyDictionary<string, string> Metadata, KustoExecutionError? Error = null);
    public sealed record KustoExecutionError(string Code, string Message, bool IsRetryable, bool? IsPermanent = null, int? FailureCode = null, string? FailureSubCode = null);
    public interface IKustoRequestBuilder
    {
        KustoExecutionRequest Build(JobDefinition job, SliceRange slice);
        KustoExecutionRequest Build(JobDefinition job, SliceExecutionUnit execution);
    }
    public interface IKustoExecutor { Task<KustoExecutionResult> ExecuteAsync(KustoExecutionRequest request, CancellationToken cancellationToken = default); }

    public sealed partial class KustoRequestBuilder : IKustoRequestBuilder
    {
        public KustoExecutionRequest Build(JobDefinition job, SliceRange slice) =>
            Build(job, SliceExecutionUnit.Unchunked(slice));

        public KustoExecutionRequest Build(JobDefinition job, SliceExecutionUnit execution)
        {
            if (!Uri.TryCreate(job.Target.ClusterUri, UriKind.Absolute, out var clusterUri) || clusterUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Kusto cluster URI must be an absolute https URI.");
            ValidateIdentifier(job.Target.Database, nameof(job.Target.Database)); ValidateIdentifier(job.FunctionName, nameof(job.FunctionName)); ValidateIdentifier(job.OutputTable, nameof(job.OutputTable));
            var slice = execution.Slice;
            if (!StringComparer.Ordinal.Equals(job.Id, slice.JobId)) throw new InvalidOperationException("Slice job id must match the job id.");
            ValidateChunkIdentity(job, execution);

            var sliceKey = slice.ToKey().Value;
            var executionKey = execution.ExecutionKey;
            var idempotencyKey = $"ko-lite:{executionKey}";
            var ingestByTag = $"ingest-by:{idempotencyKey}";
            var ingestIfNotExists = KustoString(JsonSerializer.Serialize(new[] { idempotencyKey }));
            var tags = KustoString(JsonSerializer.Serialize(new[] { ingestByTag }));
            var commandText = string.Create(CultureInfo.InvariantCulture, $$"""
                .set-or-append {{job.OutputTable}} with (ingestIfNotExists = {{ingestIfNotExists}}, tags = {{tags}}) <|
                {{job.FunctionName}}({{BuildFunctionArguments(job, execution)}})
                """);

            return new KustoExecutionRequest(
                clusterUri,
                job.Target.Database,
                job.FunctionName,
                job.OutputTable,
                slice,
                sliceKey,
                executionKey,
                execution.ChunkId,
                execution.TotalChunks,
                $"output|{executionKey}",
                idempotencyKey,
                ingestByTag,
                commandText,
                job.QueryTimeout);
        }

        private static string BuildFunctionArguments(JobDefinition job, SliceExecutionUnit execution)
        {
            var slice = execution.Slice;
            var start = FormatDateTime(slice.StartUtc);
            var end = FormatDateTime(slice.EndUtc);
            var arguments = $"datetime({start}), datetime({end})";
            if (execution.IsChunked)
            {
                arguments += string.Create(CultureInfo.InvariantCulture, $", {execution.ChunkId!.Value}, {execution.TotalChunks!.Value}");
            }

            if (job.JobSettings is { } settings && HasNonEmptyJobSettings(settings))
            {
                arguments += $", dynamic({settings.GetRawText()})";
            }

            return arguments;
        }

        private static void ValidateChunkIdentity(JobDefinition job, SliceExecutionUnit execution)
        {
            if (job.Chunks is null && execution.IsChunked)
            {
                throw new InvalidOperationException("Chunk metadata cannot be supplied for a job without chunks.");
            }

            if (job.Chunks is { } chunks
                && (!execution.IsChunked || execution.TotalChunks != chunks))
            {
                throw new InvalidOperationException($"Chunked job '{job.ActivityId}' requires execution metadata with total chunks {chunks}.");
            }
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
            => await ExecuteAsync(job, SliceExecutionUnit.Unchunked(slice), cancellationToken).ConfigureAwait(false);

        public async Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceExecutionUnit execution, CancellationToken cancellationToken = default)
        {
            KustoExecutionRequest request;
            try
            {
                request = requestBuilder.Build(job, execution);
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
            return LocalSliceOutputResult.Failure(error.Code, error.Message, error.IsRetryable, error.IsPermanent, error.FailureCode, error.FailureSubCode);
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
            var error = KustoErrorClassifier.Classify(exception);
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

    internal static partial class KustoRemoteErrorPermanenceDetector
    {
        private const string CrossClusterFailureMarker = "Cross-cluster query failure (From remote cluster:";

        public static bool HasExplicitNonPermanentSignal(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                var markerIndex = current.Message.IndexOf(CrossClusterFailureMarker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var errorProperty = RemoteErrorPropertyRegex().Match(current.Message, markerIndex);
                if (!errorProperty.Success)
                {
                    continue;
                }

                var escapedQuote = errorProperty.Groups["escape"].Value + "\"";
                var permanenceProperty = escapedQuote + "@permanent" + escapedQuote;
                var permanenceIndex = current.Message.IndexOf(
                    permanenceProperty,
                    errorProperty.Index + errorProperty.Length,
                    StringComparison.OrdinalIgnoreCase);
                if (permanenceIndex < 0)
                {
                    continue;
                }

                var permanenceValue = current.Message.AsSpan(permanenceIndex + permanenceProperty.Length).TrimStart();
                if (!permanenceValue.IsEmpty
                    && permanenceValue[0] == ':'
                    && IsExplicitFalse(permanenceValue[1..]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExplicitFalse(ReadOnlySpan<char> value)
        {
            value = value.TrimStart();
            if (!value.StartsWith("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return value.Length == "false".Length
                || value["false".Length] is ',' or '}' or '\\'
                || char.IsWhiteSpace(value["false".Length]);
        }

        [GeneratedRegex(@"(?<escape>\\*)""error\k<escape>""\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RemoteErrorPropertyRegex();
    }

    // Retryability normally comes from the Kusto SDK's permanence flag. Cross-cluster requests
    // can wrap a remote transient failure in an outer permanent HTTP 400 exception, so a
    // recognized remote envelope's explicit nested permanence field takes precedence.
    public static class KustoErrorClassifier
    {
        public static KustoExecutionError Classify(KustoException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            var isPermanent = exception.IsPermanent
                && !KustoRemoteErrorPermanenceDetector.HasExplicitNonPermanentSignal(exception);
            return new KustoExecutionError(
                exception.GetType().Name,
                exception.Message,
                IsRetryable: !isPermanent,
                IsPermanent: isPermanent,
                FailureCode: exception.FailureCode,
                FailureSubCode: exception.FailureSubCode);
        }

        // Fallback for failures reported without a Kusto exception to inspect. There is no
        // permanence signal available here, so the slice stays retryable and is bounded by the
        // worker's MaxAttempts, matching how the worker already treats unclassified faults.
        public static KustoExecutionError Classify(string code, string message) =>
            new(code, message, IsRetryable: true);
    }
}
