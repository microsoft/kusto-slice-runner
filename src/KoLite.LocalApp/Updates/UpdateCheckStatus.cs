namespace KoLite.LocalApp.Updates
{
    public enum UpdateCheckStatus
    {
        Checking,
        UpToDate,
        UpdateAvailable,
        Ahead,
        Diverged,
        Unavailable
    }

    public enum UpdateCheckUnavailableReason
    {
        None,
        Disabled,
        NoBuildSha,
        GhMissing,
        GhNotAuthenticated,
        RepoAccessDenied,
        NetworkError,
        Unknown
    }
}
