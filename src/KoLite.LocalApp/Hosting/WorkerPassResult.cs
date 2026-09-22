// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace KoLite.LocalApp
{
    internal sealed record WorkerPassResult(bool ClaimedWork, bool Succeeded, bool DeadLettered);
}
