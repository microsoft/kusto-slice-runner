// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KoLite.LocalApp.Http
{
    public interface ILocalRequestPolicy
    {
        bool IsAllowed(HttpContext context);
    }

    public sealed class LoopbackLocalRequestPolicy : ILocalRequestPolicy
    {
        public bool IsAllowed(HttpContext context)
        {
            var address = context.Connection.RemoteIpAddress;
            return address is not null && IPAddress.IsLoopback(address);
        }
    }

    public sealed class LocalRequestEndpointFilter : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var policy = context.HttpContext.RequestServices.GetRequiredService<ILocalRequestPolicy>();
            if (!policy.IsAllowed(context.HttpContext))
            {
                ProblemHttpResult problem = HttpProblem.Create(
                    StatusCodes.Status403Forbidden,
                    "local-request-required",
                    "A local request is required.",
                    "This endpoint is available only to requests originating from the local machine.");
                return ValueTask.FromResult<object?>(problem);
            }

            return next(context);
        }
    }

    public sealed class LocalRequestMiddleware
    {
        private readonly RequestDelegate next;

        public LocalRequestMiddleware(RequestDelegate next)
        {
            this.next = next;
        }

        public async Task InvokeAsync(
            HttpContext context,
            ILocalRequestPolicy policy,
            IProblemDetailsService problemDetailsService)
        {
            if (policy.IsAllowed(context))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var details = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "A local request is required.",
                Detail = "This endpoint is available only to requests originating from the local machine."
            };
            details.Extensions["code"] = "local-request-required";
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = details
            });
        }
    }
}
