// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace Ksr.Local.Kusto.Execution
{
    public enum KsrKustoAuthMode
    {
        AzureCli,
        ManagedIdentity
    }

    public sealed class KsrKustoOptions
    {
        public KsrKustoAuthMode AuthMode { get; init; } = KsrKustoAuthMode.AzureCli;
        public string? ManagedIdentityClientId { get; init; }
    }

    public sealed record KsrKustoConnectionDescriptor(
        string ClusterUri,
        string Database,
        KsrKustoAuthMode AuthMode,
        string? ManagedIdentityClientId);

    public interface IKustoControlCommandClient : IDisposable
    {
        Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken);
    }

    public interface IKustoControlCommandClientFactory
    {
        IKustoControlCommandClient Create(KustoExecutionRequest request);
        IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database);
        KsrKustoConnectionDescriptor Describe(KustoExecutionRequest request);
    }

    public sealed class KsrKustoConnectionFactory : IKustoControlCommandClientFactory
    {
        private readonly KsrKustoOptions options;

        public KsrKustoConnectionFactory(KsrKustoOptions options)
        {
            this.options = options;
        }

        public IKustoControlCommandClient Create(KustoExecutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var provider = KustoClientFactory.CreateCslAdminProvider(BuildConnectionString(request, options));
            return new KustoSdkControlCommandClient(provider);
        }

        public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database)
        {
            ArgumentNullException.ThrowIfNull(clusterUri);
            if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("Database is required.", nameof(database));
            var provider = KustoClientFactory.CreateCslAdminProvider(BuildConnectionString(clusterUri.ToString(), database, options));
            return new KustoSdkControlCommandClient(provider);
        }

        public KsrKustoConnectionDescriptor Describe(KustoExecutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new KsrKustoConnectionDescriptor(
                request.ClusterUri.ToString().TrimEnd('/'),
                request.Database,
                options.AuthMode,
                string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ? null : options.ManagedIdentityClientId);
        }

        public static KustoConnectionStringBuilder BuildConnectionString(KustoExecutionRequest request, KsrKustoOptions options)
        {
            ArgumentNullException.ThrowIfNull(request);
            return BuildConnectionString(request.ClusterUri.ToString(), request.Database, options);
        }

        public static KustoConnectionStringBuilder BuildConnectionString(string clusterUri, string database, KsrKustoOptions options)
        {
            ArgumentNullException.ThrowIfNull(clusterUri);
            ArgumentNullException.ThrowIfNull(options);

            var builder = new KustoConnectionStringBuilder(clusterUri, database);
            return options.AuthMode switch
            {
                KsrKustoAuthMode.AzureCli => builder.WithAadAzCliAuthentication(interactive: false),
                KsrKustoAuthMode.ManagedIdentity when string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) =>
                    builder.WithAadSystemManagedIdentity(),
                KsrKustoAuthMode.ManagedIdentity =>
                    builder.WithAadUserManagedIdentity(options.ManagedIdentityClientId),
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.AuthMode, "Unsupported Kusto auth mode.")
            };
        }
    }

    internal sealed class KustoSdkControlCommandClient : IKustoControlCommandClient
    {
        private readonly ICslAdminProvider adminProvider;

        public KustoSdkControlCommandClient(ICslAdminProvider adminProvider)
        {
            this.adminProvider = adminProvider;
        }

        public async Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken)
        {
            return await adminProvider.ExecuteControlCommandAsync(database, commandText, properties).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => adminProvider.Dispose();
    }
}
