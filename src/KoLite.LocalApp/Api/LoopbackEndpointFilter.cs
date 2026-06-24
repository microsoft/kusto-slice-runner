namespace KoLite.LocalApp.Api
{
    // Single loopback guard for the entire /api route group. Applied once via AddEndpointFilter so
    // every current and future /api endpoint returns 403 for non-loopback callers without repeating
    // the check per endpoint. Uses LocalApiGuard so the same-machine policy stays defined in one place.
    public sealed class LoopbackEndpointFilter : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!LocalApiGuard.IsLoopback(context.HttpContext.Connection.RemoteIpAddress))
            {
                return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
            }

            return next(context);
        }
    }
}
