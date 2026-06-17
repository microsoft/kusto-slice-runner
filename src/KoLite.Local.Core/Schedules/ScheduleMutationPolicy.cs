using System.Globalization;

namespace KoLite.Local.Core.Schedules
{
    public sealed record ScheduleMutationViolation(string Field, string Message);

    public static class ScheduleMutationPolicy
    {
        public static IReadOnlyList<ScheduleMutationViolation> ValidateUpdate(JobDefinition current, JobDefinition proposed, bool hasStarted)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(proposed);

            var violations = new List<ScheduleMutationViolation>();
            if (!StringComparer.Ordinal.Equals(current.Id, proposed.Id))
            {
                violations.Add(new ScheduleMutationViolation(
                    "id",
                    $"id cannot change from '{current.Id}' to '{proposed.Id}' for an existing job."));
            }

            if (!hasStarted)
            {
                return violations;
            }

            if (current.QueryWindowSize != proposed.QueryWindowSize)
            {
                violations.Add(Violation("queryWindowSize", Format(current.QueryWindowSize), Format(proposed.QueryWindowSize), current.ActivityId));
            }

            if (current.StartFrom.ToUniversalTime() != proposed.StartFrom.ToUniversalTime())
            {
                violations.Add(Violation("startFrom", Format(current.StartFrom), Format(proposed.StartFrom), current.ActivityId));
            }

            return violations;
        }

        private static ScheduleMutationViolation Violation(string field, string current, string proposed, string activityId) =>
            new(field, $"{field} cannot change from '{current}' to '{proposed}' after job '{activityId}' has started.");

        private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

        private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
