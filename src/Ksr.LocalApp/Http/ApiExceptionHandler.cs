// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Ksr.LocalApp.Http
{
    public sealed class ApiExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService problemDetailsService;

        public ApiExceptionHandler(IProblemDetailsService problemDetailsService)
        {
            this.problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (!IsJsonSurface(httpContext.Request.Path))
            {
                return false;
            }

            var problem = exception as ApplicationProblemException
                ?? (exception is AntiforgeryValidationException
                    ? new ApplicationProblemException(
                        StatusCodes.Status400BadRequest,
                        "antiforgery-validation-failed",
                        "The request verification token is invalid.",
                        "Refresh the page and retry the request.",
                        innerException: exception)
                    : new ApplicationProblemException(
                    StatusCodes.Status500InternalServerError,
                    "internal-error",
                    "An unexpected error occurred.",
                    "Kusto Slice Runner could not complete the request.",
                    innerException: exception));
            httpContext.Response.StatusCode = problem.StatusCode;
            var details = new ProblemDetails
            {
                Status = problem.StatusCode,
                Title = problem.Title,
                Detail = problem.Message
            };
            details.Extensions["code"] = problem.Code;
            foreach (var extension in problem.Extensions)
            {
                details.Extensions[extension.Key] = extension.Value;
            }

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = details
            });
        }

        private static bool IsJsonSurface(PathString path)
        {
            return path.StartsWithSegments("/api/v1")
                || path.StartsWithSegments("/ui-api/v1")
                || path.StartsWithSegments("/control/v1");
        }
    }
}
