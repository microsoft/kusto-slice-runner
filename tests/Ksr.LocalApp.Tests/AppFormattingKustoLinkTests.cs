// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Ui;

namespace Ksr.LocalApp.Tests
{
    public sealed class AppFormattingKustoLinkTests
    {
        [Fact]
        public void KustoQuoteName_wraps_in_bracket_quoted_form()
        {
            Assert.Equal("['MyTable']", AppFormatting.KustoQuoteName("MyTable"));
        }

        [Fact]
        public void KustoQuoteName_preserves_dotted_names()
        {
            Assert.Equal("['ns.Table']", AppFormatting.KustoQuoteName("ns.Table"));
        }

        [Fact]
        public void KustoQuoteName_escapes_backslash_then_single_quote()
        {
            Assert.Equal("['a\\'b']", AppFormatting.KustoQuoteName("a'b"));
            Assert.Equal("['a\\\\b']", AppFormatting.KustoQuoteName("a\\b"));
        }

        [Fact]
        public void KustoWebExplorerLink_uses_cluster_host_and_uri_encodes_db_and_query()
        {
            var link = AppFormatting.KustoWebExplorerLink("https://demo.kusto.windows.net", "My Db", "print 1");
            Assert.Equal(
                "https://dataexplorer.azure.com/clusters/demo.kusto.windows.net/databases/My%20Db?query=print%201",
                link);
        }

        [Fact]
        public void KustoWebExplorerLink_falls_back_to_raw_value_when_cluster_uri_is_not_absolute()
        {
            var link = AppFormatting.KustoWebExplorerLink("demo", "DemoDb", "print 1");
            Assert.Equal(
                "https://dataexplorer.azure.com/clusters/demo/databases/DemoDb?query=print%201",
                link);
        }

        [Fact]
        public void KustoShowFunctionLink_builds_quoted_show_function_command()
        {
            var link = AppFormatting.KustoShowFunctionLink("https://demo.kusto.windows.net", "DemoDb", "MyFunction");
            Assert.Equal(
                "https://dataexplorer.azure.com/clusters/demo.kusto.windows.net/databases/DemoDb?query=.show%20function%20%5B%27MyFunction%27%5D",
                link);
        }

        [Fact]
        public void KustoTablePreviewLink_builds_quoted_take_preview_query()
        {
            var link = AppFormatting.KustoTablePreviewLink("https://demo.kusto.windows.net", "DemoDb", "MyTable");
            Assert.Equal(
                "https://dataexplorer.azure.com/clusters/demo.kusto.windows.net/databases/DemoDb?query=%5B%27MyTable%27%5D%20%7C%20take%2010",
                link);
        }
    }
}
