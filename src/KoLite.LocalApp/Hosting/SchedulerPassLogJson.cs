// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace KoLite.LocalApp
{
    internal static class SchedulerPassLogJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
    }
}
