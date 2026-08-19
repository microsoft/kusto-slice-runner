using KoLite.LocalApp.Http;

namespace KoLite.LocalApp.Http.AgentApi
{
    public static class AgentApiEndpoints
    {
        public static RouteGroupBuilder MapAgentApi(this WebApplication app)
        {
            var api = app.MapGroup("/api/v1")
                .AddEndpointFilter<LocalRequestEndpointFilter>();
            JobsEndpoints.Map(api);
            RepairEndpoints.Map(api);
            OperationsEndpoints.Map(api);
            LineageEndpoints.Map(api);
            SystemEndpoints.Map(api);
            return api;
        }
    }
}
