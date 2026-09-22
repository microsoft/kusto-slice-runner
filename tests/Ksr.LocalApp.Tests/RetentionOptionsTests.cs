// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Retention;
using Microsoft.Extensions.Configuration;

namespace Ksr.LocalApp.Tests
{
    public sealed class RetentionOptionsTests
    {
        [Fact]
        public void Options_default_to_enabled_thirty_day_window()
        {
            var options = LocalRetentionOptions.From(new ConfigurationBuilder().Build());

            Assert.True(options.Enabled);
            Assert.Equal(TimeSpan.FromDays(30), options.Window);
            Assert.Equal(TimeSpan.FromHours(6), options.Interval);
            Assert.Equal(TimeSpan.FromMinutes(2), options.InitialDelay);
            Assert.Equal(2000, options.BatchSize);
        }

        [Fact]
        public void Options_honor_explicit_overrides()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ksr:Retention:Enabled"] = "false",
                    ["Ksr:Retention:WindowDays"] = "60",
                    ["Ksr:Retention:Interval"] = "12:00:00",
                    ["Ksr:Retention:InitialDelay"] = "00:00:30",
                    ["Ksr:Retention:BatchSize"] = "500"
                })
                .Build();

            var options = LocalRetentionOptions.From(configuration);

            Assert.False(options.Enabled);
            Assert.Equal(TimeSpan.FromDays(60), options.Window);
            Assert.Equal(TimeSpan.FromHours(12), options.Interval);
            Assert.Equal(TimeSpan.FromSeconds(30), options.InitialDelay);
            Assert.Equal(500, options.BatchSize);
        }

        [Fact]
        public void Protected_window_never_drops_below_the_max_chart_range()
        {
            var shortWindow = LocalRetentionOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Ksr:Retention:WindowDays"] = "7" })
                .Build());
            Assert.Equal(TimeSpan.FromDays(30), shortWindow.ProtectedWindow);

            var longWindow = LocalRetentionOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Ksr:Retention:WindowDays"] = "90" })
                .Build());
            Assert.Equal(TimeSpan.FromDays(90), longWindow.ProtectedWindow);
        }

        [Theory]
        [InlineData("Ksr:Retention:WindowDays", "0")]
        [InlineData("Ksr:Retention:WindowDays", "-5")]
        [InlineData("Ksr:Retention:Interval", "00:00:00")]
        [InlineData("Ksr:Retention:BatchSize", "0")]
        [InlineData("Ksr:Retention:BatchSize", "-1")]
        public void Options_reject_invalid_values(string key, string value)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
                .Build();

            Assert.Throws<InvalidOperationException>(() => LocalRetentionOptions.From(configuration));
        }
    }
}
