namespace AlphaIsland.Plugins.VivoOffice;

/// <summary>两秒巡检只对新的连接证据提醒；历史快照和写入中的记录不制造事件。</summary>
internal sealed class VivoConnectionEvents
{
    private VivoConnection? _previous;
    private VivoConnection? _awaitingOnlineConfirmation;
    private DateTimeOffset _noticeAt;

    internal bool Observe(VivoConnection current, DateTimeOffset now)
    {
        if (current.Notice.Length > 0 && current.DeviceName.Length > 0) return false;
        var previous = _previous;
        _previous = current;
        if (previous == null || current.DeviceName.Length == 0) return false;
        bool onlineTransition = current.IsCurrent && (!previous.IsCurrent || current.DeviceName != previous.DeviceName);
        bool newSuccess = current.ConnectedAt is { } stamp && (previous.ConnectedAt == null || stamp > previous.ConnectedAt)
            && stamp >= now.AddMinutes(-2) && stamp <= now.AddMinutes(1);
        bool confirmingNotice = current.IsCurrent && _awaitingOnlineConfirmation is { } awaiting
            && current.DeviceName == awaiting.DeviceName && current.ConnectedAt == awaiting.ConnectedAt
            && now - _noticeAt < TimeSpan.FromMinutes(2);
        if (confirmingNotice) _awaitingOnlineConfirmation = null;
        if ((!onlineTransition && !newSuccess) || (confirmingNotice && !newSuccess)) return false;
        _awaitingOnlineConfirmation = current.IsCurrent ? null : current;
        _noticeAt = now;
        return true;
    }
}
