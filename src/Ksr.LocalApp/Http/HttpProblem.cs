// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Application;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ksr.LocalApp.Http
{
    internal static class HttpProblem
    {
        public static ProblemHttpResult From(ApplicationProblemException exception)
        {
            var extensions = new Dictionary<string, object?>(exception.Extensions, StringComparer.Ordinal)
            {
                ["code"] = exception.Code
            };
            return TypedResults.Problem(
                detail: exception.Message,
                statusCode: exception.StatusCode,
                title: exception.Title,
                extensions: extensions);
        }

        public static ProblemHttpResult Create(
            int statusCode,
            string code,
            string title,
            string detail,
            IReadOnlyDictionary<string, object?>? extensions = null)
        {
            var values = extensions is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(extensions, StringComparer.Ordinal);
            values["code"] = code;
            return TypedResults.Problem(
                detail: detail,
                statusCode: statusCode,
                title: title,
                extensions: values);
        }
    }
}
