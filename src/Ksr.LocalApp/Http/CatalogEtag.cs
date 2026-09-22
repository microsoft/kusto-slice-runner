// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Application;

namespace Ksr.LocalApp.Http
{
    internal static class CatalogEtag
    {
        public static string Format(long catalogVersion)
        {
            return $"\"catalog-{catalogVersion}\"";
        }

        public static long RequireMatch(HttpRequest request, long currentVersion)
        {
            if (!request.Headers.TryGetValue("If-Match", out var values) || values.Count == 0)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status428PreconditionRequired,
                    "if-match-required",
                    "An If-Match precondition is required.",
                    "Read the job, then send its ETag in the If-Match header.");
            }

            var expected = Format(currentVersion);
            var supplied = values
                .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
            if (!supplied.Contains(expected, StringComparer.Ordinal))
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status412PreconditionFailed,
                    "etag-mismatch",
                    "The job changed since it was read.",
                    "The supplied If-Match value does not match the current job version.",
                    new Dictionary<string, object?>
                    {
                        ["currentVersion"] = currentVersion,
                        ["currentEtag"] = expected
                    });
            }

            return currentVersion;
        }

        public static void Write(HttpResponse response, long catalogVersion)
        {
            response.Headers.ETag = Format(catalogVersion);
        }
    }
}
