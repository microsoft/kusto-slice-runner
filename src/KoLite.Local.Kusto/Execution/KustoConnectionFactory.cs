using System.Data;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace KoLite.Local.Kusto.Execution
{
    public enum KoLiteKustoAuthMode
    {
        AzureCli,
        ManagedIdentity
    }

    public sealed class KoLiteKustoOptions
    {
        public KoLiteKustoAuthMode AuthMode { get; init; } = KoLiteKustoAuthMode.AzureCli;
        public string? ManagedIdentityClientId { get; init; }
    }

    public sealed record KoLiteKustoConnectionDescriptor(
        string ClusterUri,
        string Database,
        KoLiteKustoAuthMode AuthMode,
        string? ManagedIdentityClientId);

    public interface IKustoControlCommandClient : IDisposable
    {
        Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken);
    }

    public interface IKustoControlCommandClientFactory
    {
        IKustoControlCommandClient Create(KustoExecutionRequest request);
        KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request);
    }

    public sealed class KoLiteKustoConnectionFactory : IKustoControlCommandClientFactory
    {
        private readonly KoLiteKustoOptions options;

        public KoLiteKustoConnectionFactory(KoLiteKustoOptions options)
        {
            this.options = options;
        }

        public IKustoControlCommandClient Create(KustoExecutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var provider = KustoClientFactory.CreateCslAdminProvider(BuildConnectionString(request, options));
            return new KustoSdkControlCommandClient(provider);
        }

        public KoLiteKustoConnectionDescriptor Describe(KustoExecutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return new KoLiteKustoConnectionDescriptor(
                request.ClusterUri.ToString().TrimEnd('/'),
                request.Database,
                options.AuthMode,
                string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ? null : options.ManagedIdentityClientId);
        }

        public static KustoConnectionStringBuilder BuildConnectionString(KustoExecutionRequest request, KoLiteKustoOptions options)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(options);

            var builder = new KustoConnectionStringBuilder(request.ClusterUri.ToString(), request.Database);
            return options.AuthMode switch
            {
                KoLiteKustoAuthMode.AzureCli => builder.WithAadAzCliAuthentication(interactive: false),
                KoLiteKustoAuthMode.ManagedIdentity when string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) =>
                    builder.WithAadSystemManagedIdentity(),
                KoLiteKustoAuthMode.ManagedIdentity =>
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
