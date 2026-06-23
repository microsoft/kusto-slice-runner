namespace KoLite.LocalApp
{
    internal sealed record WorkerPassResult(bool ClaimedWork, bool Succeeded, bool DeadLettered);
}
