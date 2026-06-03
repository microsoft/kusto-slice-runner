using System.Text.Json;

namespace KoLite.Local.Core.Schedules
{
    public sealed record JobDefinition
    {
        public required string ActivityId { get; init; }
        public required string FunctionName { get; init; }
        public required string OutputTable { get; init; }
        public required TimeSpan QueryWindowSize { get; init; }
        public required TimeSpan DelayFromUtcNow { get; init; }
        public required int MaxParallelism { get; init; }
        public required TimeSpan QueryTimeout { get; init; }
        public required DateTimeOffset StartFrom { get; init; }
        public required JobTarget Target { get; init; }
        public bool IsPaused { get; init; }
        public DateTimeOffset? EndOn { get; init; }
        public string? Folder { get; init; }
        public IReadOnlyList<DependentJob> DependsOn { get; init; } = Array.Empty<DependentJob>();
        public JsonElement? JobSettings { get; init; }
    }

    public sealed record JobTarget
    {
        public required string ClusterUri { get; init; }
        public required string Database { get; init; }
    }

    public sealed record DependentJob
    {
        public required string ActivityId { get; init; }
    }
}
