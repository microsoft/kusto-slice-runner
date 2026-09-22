// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Ui;

namespace Ksr.LocalApp.Tests
{
    public sealed class AppFormattingTests
    {
        [Fact]
        public void Iso_normalizes_non_utc_offsets_to_z()
        {
            var value = new DateTimeOffset(2026, 8, 19, 22, 0, 0, TimeSpan.FromHours(-7));

            Assert.Equal("2026-08-20T05:00:00Z", AppFormatting.Iso(value));
        }

        [Fact]
        public void Nullable_iso_uses_dash_for_missing_values()
        {
            DateTimeOffset? value = null;

            Assert.Equal("-", AppFormatting.Iso(value));
        }
    }
}
