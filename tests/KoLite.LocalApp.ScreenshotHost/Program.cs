using System.Globalization;
using System.Net;
using System.Text.Json;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KoLite.LocalApp.ScreenshotHost
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length != 3
                || !Guid.TryParseExact(args[1], "N", out var runId)
                || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                throw new ArgumentException("Usage: KoLite.LocalApp.ScreenshotHost <workspace> <new-run-guid-N> <port>");
            }

            var sandbox = ScreenshotSandbox.Create(args[0], runId, port);
            var connections = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(sandbox.DatabasePath));
            new KoLiteSqliteSchema(connections).EnsureSchema();
            ScreenshotDataset.Seed(connections);

            await using var factory = new ScreenshotAppFactory(sandbox);
            factory.UseKestrel(options => options.Listen(IPAddress.Loopback, sandbox.Port));
            factory.StartServer();

            var pendingManifestPath = Path.Combine(sandbox.RunDirectory, "manifest.pending");
            using (var manifest = new FileStream(pendingManifestPath, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(manifest, new
                {
                    schemaVersion = 1,
                    sandbox.RunId,
                    sandbox.BaseUrl,
                    sandbox.DatabasePath,
                    sandbox.ImagesDirectory,
                    processId = Environment.ProcessId,
                    nowUtc = ScreenshotDataset.Now,
                    detailJobId = ScreenshotDataset.DetailJobId,
                    failureJobId = ScreenshotDataset.FailureJobId,
                    activityIds = ScreenshotDataset.Jobs.Select(job => job.ActivityId)
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }
            File.Move(pendingManifestPath, sandbox.ManifestPath);
            Console.WriteLine($"Screenshot fixture ready: {sandbox.BaseUrl}");
            Console.WriteLine($"Capture manifest: {sandbox.ManifestPath}");

            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());
            await stopped.Task;

            var blocked = factory.Services.GetRequiredService<ForbiddenExternalServices>();
            if (blocked.Attempts != 0)
            {
                throw new InvalidOperationException($"Capture attempted {blocked.Attempts} forbidden external operation(s).");
            }
        }
    }
}
