// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.LocalApp
{
    internal sealed record WorkerPassResult(bool ClaimedWork, bool Succeeded, bool DeadLettered);
}
