using System.Globalization;
using System.Text.Json;

namespace KoLite.LocalApp.ScreenshotHost
{
    public sealed class ScreenshotSandbox
    {
        private ScreenshotSandbox(string workspace, Guid runId, int port)
        {
            Workspace = workspace;
            RunId = runId.ToString("N");
            Port = port;
            RunDirectory = Path.Combine(workspace, "artifacts", "documentation-screenshots", "runs", RunId);
        }

        public string Workspace { get; }
        public string RunId { get; }
        public int Port { get; }
        public string RunDirectory { get; }
        public string DatabasePath => Path.Combine(RunDirectory, "screenshots.db");
        public string ImagesDirectory => Path.Combine(RunDirectory, "images");
        public string ManifestPath => Path.Combine(RunDirectory, "manifest.json");
        public string BaseUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

        public static ScreenshotSandbox Create(string workspace, Guid runId, int port)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
            if (port is < 1024 or > 65535 || port == 5057)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Use a separate unprivileged port, never the live default 5057.");
            }
            if (runId == Guid.Empty)
            {
                throw new ArgumentException("A new nonempty run ID is required.", nameof(runId));
            }

            workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (!File.Exists(Path.Combine(workspace, "KoLite.Local.sln"))
                || !File.Exists(Path.Combine(workspace, "src", "KoLite.LocalApp", "KoLite.LocalApp.csproj")))
            {
                throw new ArgumentException("Workspace must be an explicit KO Lite source checkout.", nameof(workspace));
            }

            var sandbox = new ScreenshotSandbox(workspace, runId, port);
            for (var directory = new DirectoryInfo(sandbox.RunDirectory); directory is not null; directory = directory.Parent)
            {
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"Screenshot sandbox paths must not traverse links: {directory.FullName}");
                }
            }
            if (Directory.Exists(sandbox.RunDirectory) || File.Exists(sandbox.RunDirectory))
            {
                throw new IOException("The screenshot run already exists. Choose a new run ID; existing data is never reset.");
            }

            Directory.CreateDirectory(sandbox.RunDirectory);
            using (var marker = new FileStream(Path.Combine(sandbox.RunDirectory, "owner.json"), FileMode.CreateNew))
            {
                JsonSerializer.Serialize(marker, new { purpose = "ko-lite-documentation-screenshots", runId = sandbox.RunId });
            }
            using (new FileStream(sandbox.DatabasePath, FileMode.CreateNew)) { }
            Directory.CreateDirectory(sandbox.ImagesDirectory);
            return sandbox;
        }
    }
}
