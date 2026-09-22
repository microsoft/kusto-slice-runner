// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Connections;
using Ksr.LocalApp.Http.AgentApi;

namespace Ksr.LocalApp.Http
{
    public static class HealthEndpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet("/healthz", (IKsrSqliteConnectionFactory connections) =>
                {
                    using var connection = connections.OpenConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1;";
                    command.ExecuteScalar();
                    return TypedResults.Ok(new HealthResponse("healthy"));
                })
                .WithName("HealthProbe")
                .ExcludeFromDescription();
        }
    }
}
