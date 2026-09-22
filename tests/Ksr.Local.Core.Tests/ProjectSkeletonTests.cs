// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.Local.Core.Tests
{
    public sealed class ProjectSkeletonTests
    {
        [Fact]
        public void Solution_contains_expected_local_first_projects()
        {
            var root = FindSolutionRoot();
            var expected = new[]
            {
                "src\\Ksr.Local.Core\\Ksr.Local.Core.csproj",
                "src\\Ksr.Local.Sqlite\\Ksr.Local.Sqlite.csproj",
                "src\\Ksr.Local.Kusto\\Ksr.Local.Kusto.csproj",
                "src\\Ksr.LocalApp\\Ksr.LocalApp.csproj",
                "tests\\Ksr.Local.Core.Tests\\Ksr.Local.Core.Tests.csproj",
                "tests\\Ksr.Local.Sqlite.Tests\\Ksr.Local.Sqlite.Tests.csproj",
                "tests\\Ksr.Local.Kusto.Tests\\Ksr.Local.Kusto.Tests.csproj",
                "tests\\Ksr.LocalApp.Tests\\Ksr.LocalApp.Tests.csproj"
            };

            Assert.True(File.Exists(Path.Combine(root, "Ksr.Local.sln")));
            foreach (var path in expected)
            {
                Assert.True(File.Exists(Path.Combine(root, path)), $"Missing {path}");
            }
        }

        private static string FindSolutionRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ksr.Local.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
