// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.LocalApp.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ksr.LocalApp.Tests
{
    internal sealed class TestLocalRequestPolicy : ILocalRequestPolicy
    {
        public bool IsAllowed(HttpContext context)
        {
            return true;
        }
    }

    internal static class TestLocalRequestPolicyExtensions
    {
        public static void AddTestLocalRequestPolicy(this IServiceCollection services)
        {
            services.RemoveAll<ILocalRequestPolicy>();
            services.AddSingleton<ILocalRequestPolicy, TestLocalRequestPolicy>();
        }
    }
}
