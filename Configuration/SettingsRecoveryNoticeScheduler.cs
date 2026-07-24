namespace MidFD.Configuration;

internal enum SettingsRecoveryNoticeAction { None, ScheduleShown, Show }

internal sealed class SettingsRecoveryNoticeScheduler
{
    private bool _scheduled;
    private bool _shown;

    public SettingsRecoveryNoticeAction Evaluate(bool hasRecovery, bool isHandleCreated, bool unavailable)
    {
        if (!hasRecovery || unavailable || _shown) return SettingsRecoveryNoticeAction.None;
        if (!isHandleCreated)
        {
            if (_scheduled) return SettingsRecoveryNoticeAction.None;
            _scheduled = true;
            return SettingsRecoveryNoticeAction.ScheduleShown;
        }
        _scheduled = false;
        _shown = true;
        return SettingsRecoveryNoticeAction.Show;
    }
}
