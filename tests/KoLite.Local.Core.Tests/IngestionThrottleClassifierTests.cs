using KoLite.Local.Core.Throttling;

namespace KoLite.Local.Core.Tests
{
    public sealed class IngestionThrottleClassifierTests
    {
        // The real message KO Lite surfaces for a TableSetOrAppend ingestion throttle (trimmed).
        private const string IngestionThrottleMessage = """
            KustoRequestThrottledException: TooManyRequests (429-TooManyRequests): {
              "error": {
                "code": "Too many requests",
                "@message": "The control command was aborted due to throttling. Retrying after some backoff might succeed. CommandType: 'TableSetOrAppend', Capacity: 18, Origin: 'CapacityPolicy/Ingestion'.",
                "@permanent": false
              }
            }
            """;

        [Fact]
        public void Ingestion_capacity_throttle_is_classified_with_reported_capacity()
        {
            var result = IngestionThrottleClassifier.Classify("KustoRequestThrottledException", IngestionThrottleMessage);

            Assert.True(result.IsIngestionCapacityThrottle);
            Assert.Equal(18, result.ReportedCapacity);
        }

        [Fact]
        public void Origin_marker_in_error_code_alone_is_sufficient()
        {
            var result = IngestionThrottleClassifier.Classify("CapacityPolicy/Ingestion", errorMessage: null);

            Assert.True(result.IsIngestionCapacityThrottle);
            Assert.Null(result.ReportedCapacity);
        }

        [Fact]
        public void Export_capacity_throttle_is_not_an_ingestion_throttle()
        {
            var message = "The control command was aborted due to throttling. CommandType: 'ExportToStorage', Capacity: 4, Origin: 'CapacityPolicy/Export'.";

            var result = IngestionThrottleClassifier.Classify("KustoRequestThrottledException", message);

            Assert.False(result.IsIngestionCapacityThrottle);
            Assert.Null(result.ReportedCapacity);
        }

        [Fact]
        public void Workload_group_request_rate_limit_is_not_an_ingestion_throttle()
        {
            var message = "The request was denied due to throttling by the workload group 'default' Request rate limit policy.";

            var result = IngestionThrottleClassifier.Classify("KustoRequestThrottledException", message);

            Assert.False(result.IsIngestionCapacityThrottle);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("KustoServiceTimeoutException", "Query execution has exceeded the allowed timeout.")]
        [InlineData("SomeOtherError", "A transient error occurred; retrying might help.")]
        public void Non_ingestion_errors_are_not_classified(string? code, string? message)
        {
            var result = IngestionThrottleClassifier.Classify(code, message);

            Assert.False(result.IsIngestionCapacityThrottle);
            Assert.Null(result.ReportedCapacity);
        }

        [Theory]
        [InlineData("Capacity: 18", 18)]
        [InlineData("Capacity = 7", 7)]
        [InlineData("\"Capacity\": 256", 256)]
        public void Reported_capacity_is_parsed_from_common_shapes(string capacityFragment, int expected)
        {
            var message = $"throttling. {capacityFragment}, Origin: 'CapacityPolicy/Ingestion'.";

            var result = IngestionThrottleClassifier.Classify("KustoRequestThrottledException", message);

            Assert.True(result.IsIngestionCapacityThrottle);
            Assert.Equal(expected, result.ReportedCapacity);
        }
    }
}
