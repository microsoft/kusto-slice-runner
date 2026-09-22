// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace KoLite.LocalApp.Pages.Catalog
{
    internal static class CatalogConflictFeedback
    {
        public const string TempDataKey = "CatalogConflictMessage";

        public const string Message = "This job changed after the page loaded, so your request was not applied. The latest version is shown below. Review it and try again.";

        public static void Save(HttpContext http, ITempDataDictionaryFactory tempDataFactory)
        {
            var tempData = tempDataFactory.GetTempData(http);
            tempData[TempDataKey] = Message;
            tempData.Save();
        }

        public static string? Read(ITempDataDictionary tempData) => tempData[TempDataKey] as string;
    }
}
