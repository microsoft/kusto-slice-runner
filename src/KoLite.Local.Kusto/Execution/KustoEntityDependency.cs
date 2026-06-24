using System.Data;
using System.Text.RegularExpressions;
using Kusto.Data.Common;
using KoLite.Local.Core.Graph;

namespace KoLite.Local.Kusto.Execution
{
    public interface IKustoEntityDependencyReader
    {
        // Runs a read-only .show databases entities against the cluster (connecting through the
        // supplied database for context) and returns intra-cluster dependency edges across every
        // database the caller can access on that cluster.
        Task<IReadOnlyList<KustoEntityEdge>> ReadAsync(Uri clusterUri, string database, CancellationToken cancellationToken = default);
    }

    public sealed partial class KustoSdkEntityDependencyReader : IKustoEntityDependencyReader
    {
        // Read-only management command. Projecting and expanding server-side keeps the payload to the
        // entity-to-entity edges we need. RemoteEntity dependencies are kept here (not filtered) so a
        // same-cluster cross-database reference survives; ParseDependency then drops the ones that
        // actually live on a different cluster.
        private const string CommandText =
            ".show databases entities with (resolveDependencies = true, resolveFunctionsSchema = true)\n" +
            "| where EntityType in ('Table', 'Function', 'MaterializedView')\n" +
            "| mv-expand dep = Dependencies\n" +
            "| extend DepType = tostring(dep.EntityType)\n" +
            "| where DepType in ('Table', 'Function', 'MaterializedView', 'RemoteEntity')\n" +
            "| project SourceDatabase = DatabaseName, SourceName = EntityName, SourceType = EntityType, DepName = tostring(dep.EntityName), DepType";

        private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

        private readonly IKustoControlCommandClientFactory clientFactory;

        public KustoSdkEntityDependencyReader(IKustoControlCommandClientFactory clientFactory) =>
            this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

        public async Task<IReadOnlyList<KustoEntityEdge>> ReadAsync(Uri clusterUri, string database, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(clusterUri);
            if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("Database is required.", nameof(database));

            using var client = clientFactory.CreateForDatabase(clusterUri, database);
            var properties = new ClientRequestProperties { ClientRequestId = $"KoLite.Local.Lineage;{Guid.NewGuid():N}" };
            properties.SetOption(ClientRequestProperties.OptionServerTimeout, CommandTimeout);

            using var reader = await client.ExecuteControlCommandAsync(database, CommandText, properties, cancellationToken).ConfigureAwait(false);
            return Parse(reader, clusterUri.Host);
        }

        internal static IReadOnlyList<KustoEntityEdge> Parse(IDataReader reader, string queriedClusterHost)
        {
            var sourceDbOrdinal = reader.GetOrdinal("SourceDatabase");
            var sourceNameOrdinal = reader.GetOrdinal("SourceName");
            var sourceTypeOrdinal = reader.GetOrdinal("SourceType");
            var depNameOrdinal = reader.GetOrdinal("DepName");
            var depTypeOrdinal = reader.GetOrdinal("DepType");

            var edges = new List<KustoEntityEdge>();
            while (reader.Read())
            {
                var sourceDatabase = ReadString(reader, sourceDbOrdinal);
                var sourceName = ReadString(reader, sourceNameOrdinal);
                var sourceType = ReadString(reader, sourceTypeOrdinal);
                var rawDep = ReadString(reader, depNameOrdinal);
                var depType = ReadString(reader, depTypeOrdinal);

                if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(rawDep))
                {
                    continue;
                }

                var dependency = ParseDependency(rawDep, sourceDatabase, queriedClusterHost);
                if (dependency is null)
                {
                    continue;
                }

                edges.Add(new KustoEntityEdge(
                    sourceDatabase,
                    sourceName,
                    sourceType,
                    dependency.Value.Database,
                    dependency.Value.Name,
                    depType));
            }

            return edges;
        }

        // Normalizes a dependency reference (bare name, database('X').name, or
        // cluster('https://host/').database('X').name) to (database, name) within the queried
        // cluster. Returns null for an unparseable reference or one hosted on a different cluster.
        internal static (string Database, string Name)? ParseDependency(string rawReference, string sourceDatabase, string queriedClusterHost)
        {
            var reference = rawReference.Trim();

            var clusterMatch = ClusterRegex().Match(reference);
            if (clusterMatch.Success)
            {
                var host = TryGetHost(clusterMatch.Groups[1].Value);
                if (host is null || !string.Equals(host, queriedClusterHost, StringComparison.OrdinalIgnoreCase))
                {
                    return null; // different cluster - not a consumer of this cluster's jobs
                }
            }

            var dbMatch = DatabaseRegex().Match(reference);
            var database = dbMatch.Success ? dbMatch.Groups[1].Value : sourceDatabase;

            var remainder = DatabaseRegex().Replace(ClusterRegex().Replace(reference, string.Empty), string.Empty);
            var name = ExtractName(remainder);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return (database, name);
        }

        private static string ExtractName(string remainder)
        {
            var wrapped = WrappedNameRegex().Match(remainder);
            var candidate = wrapped.Success ? wrapped.Groups[1].Value : remainder;
            return candidate.Trim().Trim('.').Trim().Trim('\'', '"');
        }

        private static string? TryGetHost(string clusterReference)
        {
            var value = clusterReference.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }

            if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out var prefixed))
            {
                return prefixed.Host;
            }

            return null;
        }

        private static string ReadString(IDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        [GeneratedRegex(@"cluster\(['""]?([^'""\)]+)['""]?\)", RegexOptions.CultureInvariant)]
        private static partial Regex ClusterRegex();

        [GeneratedRegex(@"database\(['""]?([^'""\)]+)['""]?\)", RegexOptions.CultureInvariant)]
        private static partial Regex DatabaseRegex();

        [GeneratedRegex(@"(?:table|function|materialized[_-]?view)\(['""]([^'""]+)['""]\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex WrappedNameRegex();
    }
}
