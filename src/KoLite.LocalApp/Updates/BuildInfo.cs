// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;

namespace KoLite.LocalApp.Updates
{
    public sealed record AppBuildVersion(string? CommitSha);

    public static class BuildInfo
    {
        public const string GitCommitShaMetadataKey = "KoLiteGitCommitSha";

        public static string? GetCommitSha()
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(metadata.Key, GitCommitShaMetadataKey, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(metadata.Value) ? null : metadata.Value.Trim();
                }
            }

            return null;
        }

        public static string ShortSha(string? sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return string.Empty;
            var trimmed = sha.Trim();
            return trimmed.Length <= 7 ? trimmed : trimmed[..7];
        }
    }
}
