// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Connections;
using KoLite.LocalApp.Http.AgentApi;

namespace KoLite.LocalApp.Http
{
    public static class HealthEndpoints
    {
        public static void Map(WebApplication app)
        {
            app.MapGet("/healthz", (IKoLiteSqliteConnectionFactory connections) =>
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
