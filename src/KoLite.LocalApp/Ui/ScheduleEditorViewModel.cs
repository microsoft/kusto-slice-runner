namespace KoLite.LocalApp.Ui
{
    public sealed record ScheduleEditorViewModel(
        string PostAction,
        ScheduleFormInput Input,
        string ScheduleJson,
        long? ExpectedVersion,
        bool ExistingJob,
        string SubmitLabel,
        bool HasStarted = false)
    {
        public bool StartedFieldsReadOnly => ExistingJob && HasStarted;
    }
}
