// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Html;

namespace KoLite.LocalApp.Ui
{
    public static class UiIcons
    {
        private static readonly HashSet<string> KnownIcons = new(System.StringComparer.Ordinal)
        {
            "edit",
            "history",
            "download",
            "copy",
            "pause",
            "play",
            "trash",
            "restore",
            "hard-delete",
            "plus",
            "upload",
            "x",
            "graph",
        };

        public static IHtmlContent Icon(string name)
        {
            var id = ResolveIconId(name);
            return new HtmlString(
                $"<svg class=\"icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#icon-{id}\"></use></svg>");
        }

        public static IHtmlContent IconOnly(string name, string label)
        {
            var encodedLabel = WebUtility.HtmlEncode(label);
            var id = ResolveIconId(name);
            return new HtmlString(
                $"<svg class=\"icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#icon-{id}\"></use></svg>" +
                $"<span class=\"visually-hidden\">{encodedLabel}</span>");
        }

        public static IHtmlContent IconText(string name, string label)
        {
            var encodedLabel = WebUtility.HtmlEncode(label);
            var id = ResolveIconId(name);
            return new HtmlString(
                $"<svg class=\"icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#icon-{id}\"></use></svg>" +
                $"<span class=\"btn-label\">{encodedLabel}</span>");
        }

        private static string ResolveIconId(string name)
        {
            if (!KnownIcons.Contains(name))
            {
                throw new System.ArgumentOutOfRangeException(nameof(name), name, "Unknown UI icon name.");
            }

            return name;
        }
    }
}
