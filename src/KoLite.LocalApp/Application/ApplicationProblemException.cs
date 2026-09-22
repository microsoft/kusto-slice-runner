// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace KoLite.LocalApp.Application
{
    public sealed class ApplicationProblemException : Exception
    {
        public ApplicationProblemException(
            int statusCode,
            string code,
            string title,
            string detail,
            IReadOnlyDictionary<string, object?>? extensions = null,
            Exception? innerException = null)
            : base(detail, innerException)
        {
            StatusCode = statusCode;
            Code = code;
            Title = title;
            Extensions = extensions ?? new Dictionary<string, object?>();
        }

        public int StatusCode { get; }

        public string Code { get; }

        public string Title { get; }

        public IReadOnlyDictionary<string, object?> Extensions { get; }
    }
}
