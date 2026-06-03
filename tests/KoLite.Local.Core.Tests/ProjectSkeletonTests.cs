namespace KoLite.Local.Core.Tests
{
    public sealed class ProjectSkeletonTests
    {
        [Fact]
        public void Solution_contains_expected_local_first_projects()
        {
            var root = FindSolutionRoot();
            var expected = new[]
            {
                "src\\KoLite.Local.Core\\KoLite.Local.Core.csproj",
                "src\\KoLite.Local.Sqlite\\KoLite.Local.Sqlite.csproj",
                "src\\KoLite.Local.Kusto\\KoLite.Local.Kusto.csproj",
                "src\\KoLite.LocalApp\\KoLite.LocalApp.csproj",
                "tests\\KoLite.Local.Core.Tests\\KoLite.Local.Core.Tests.csproj",
                "tests\\KoLite.Local.Sqlite.Tests\\KoLite.Local.Sqlite.Tests.csproj",
                "tests\\KoLite.Local.Kusto.Tests\\KoLite.Local.Kusto.Tests.csproj",
                "tests\\KoLite.LocalApp.Tests\\KoLite.LocalApp.Tests.csproj"
            };

            Assert.True(File.Exists(Path.Combine(root, "KoLite.Local.sln")));
            foreach (var path in expected)
            {
                Assert.True(File.Exists(Path.Combine(root, path)), $"Missing {path}");
            }
        }

        private static string FindSolutionRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KoLite.Local.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
