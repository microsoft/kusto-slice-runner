// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;

namespace KoLite.LocalApp.Ui
{
    public static class SampleScheduleFactory
    {
        public static string CreateJson() =>
            $$"""
            {
              "activityId": "sample.hourly.aggregate",
              "functionName": "SampleFunction",
              "outputTable": "SampleOutput",
              "queryWindowSize": "01:00:00",
              "delayFromUtcNow": "00:10:00",
              "maxParallelism": 2,
              "queryTimeout": "00:05:00",
              "isPaused": false,
              "startFrom": "{{DateTimeOffset.UtcNow.AddHours(-6).ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture)}}",
              "target": {
                "clusterUri": "https://example.kusto.windows.net",
                "database": "Samples"
              },
              "jobSettings": {}
            }
            """;
    }
}
