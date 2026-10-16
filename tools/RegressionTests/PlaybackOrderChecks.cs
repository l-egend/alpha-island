using alpha_island.Core;
using alpha_island.Services;
using Windows.Media;

internal static partial class Program
{
    private static async Task PlaybackOrderChecks()
    {
        var music = new NowPlayingInfo { ContentKind = MediaContentKind.Music, SessionId = "latency-fixture",
            CanShuffle = true, CanRepeat = true, PlaybackOrderKnown = true };
        var tracker = new MediaPlaybackOrderTracker();
        var observed = tracker.Observe(music, true, true, false, MediaPlaybackAutoRepeatMode.None);
        foreach (var target in new[] { MediaPlaybackOrder.Shuffle, MediaPlaybackOrder.RepeatTrack, MediaPlaybackOrder.Sequential })
        {
            Check(MediaPlaybackOrders.Next(observed) == target, "连续点击基于已接受的选择推进 " + target);
            tracker.Accept(music.SessionId, target, false, MediaPlaybackAutoRepeatMode.None);
            for (int poll = 0; poll < 10; poll++)
            {
                observed = tracker.Observe(observed, true, true, false, MediaPlaybackAutoRepeatMode.None);
                Check(observed.PlaybackOrder == target && observed.PlaybackOrderKnown,
                    "重复的旧播放器报告不覆盖成功选择 " + target + " " + poll);
            }
        }
        tracker.Accept(music.SessionId, MediaPlaybackOrder.Shuffle, false, MediaPlaybackAutoRepeatMode.None);
        observed = tracker.Observe(music, true, true, true, MediaPlaybackAutoRepeatMode.None);
        Check(observed.PlaybackOrder == MediaPlaybackOrder.Shuffle, "播放器回读确认成功的随机播放");
        observed = tracker.Observe(observed, true, true, false, MediaPlaybackAutoRepeatMode.Track);
        Check(observed.PlaybackOrder == MediaPlaybackOrder.RepeatTrack, "播放器内修改模式后新的回读仍可同步");
        observed = tracker.Observe(observed, true, true, false, MediaPlaybackAutoRepeatMode.None);
        Check(observed.PlaybackOrder == MediaPlaybackOrder.Sequential, "外部模式返回原来值也不被旧请求覆盖");
        Check(tracker.Observe(music with { SessionId = "another" }, true, true, false, MediaPlaybackAutoRepeatMode.None)
            .PlaybackOrder == MediaPlaybackOrder.Sequential, "模式请求不串到其他媒体会话");
        tracker.Accept(music.SessionId, MediaPlaybackOrder.RepeatTrack, null, null);
        Check(tracker.Observe(music, true, true, null, null).PlaybackOrder == MediaPlaybackOrder.RepeatTrack,
            "播放器不提供模式回读时保留最近成功选择");

        tracker.Accept(music.SessionId, MediaPlaybackOrder.Shuffle, null, MediaPlaybackAutoRepeatMode.None);
        observed = tracker.Observe(music, true, true, null, MediaPlaybackAutoRepeatMode.Track);
        Check(observed.PlaybackOrder == MediaPlaybackOrder.RepeatTrack && !observed.PlaybackOrderKnown,
            "不完整的新单曲报告更新显示，但下一次切换仍完整清理未知随机状态");
        observed = tracker.Observe(observed, true, true, null, MediaPlaybackAutoRepeatMode.Track);
        Check(observed.PlaybackOrder == MediaPlaybackOrder.RepeatTrack && !observed.PlaybackOrderKnown,
            "重复的不完整报告不能虚构全部控制状态已知");
        observed = MediaPlaybackOrders.ApplyNative(music, true, true, true, MediaPlaybackAutoRepeatMode.Track);
        Check(!observed.PlaybackOrderKnown, "原生随机和单曲同时开启时不假定已规范化为三种互斥模式");
        var requests = new List<string>();
        async Task<bool> Switch(NowPlayingInfo info, MediaPlaybackOrder target) => await MediaPlaybackOrders.ChangeAsync(info, target,
            shuffle => { requests.Add("shuffle:" + shuffle); return Task.FromResult(true); },
            repeat => { requests.Add("repeat:" + repeat); return Task.FromResult(true); }, () => true);
        Check(await Switch(music, MediaPlaybackOrder.Shuffle) && requests.SequenceEqual(new[] { "shuffle:True" }),
            "已知顺序播放只发送一个随机命令");
        requests.Clear();
        Check(await Switch(music with { PlaybackOrder = MediaPlaybackOrder.Shuffle }, MediaPlaybackOrder.RepeatTrack)
            && requests.SequenceEqual(new[] { "shuffle:False", "repeat:Track" }), "随机转单曲仍先关闭冲突状态");
        requests.Clear();
        Check(await Switch(music with { PlaybackOrder = MediaPlaybackOrder.RepeatTrack }, MediaPlaybackOrder.Sequential)
            && requests.SequenceEqual(new[] { "repeat:None" }), "单曲转顺序不重复关闭已经关闭的随机播放");
        requests.Clear();
        Check(await Switch(music with { PlaybackOrderKnown = false }, MediaPlaybackOrder.Shuffle)
            && requests.SequenceEqual(new[] { "repeat:None", "shuffle:True" }), "初始模式未知时保留完整安全切换");
        requests.Clear();
        Check(await Switch(music, MediaPlaybackOrder.Sequential) && requests.Count == 0, "请求已经确认的模式不重复发命令");
        bool current = true;
        requests.Clear();
        bool accepted = await MediaPlaybackOrders.ChangeAsync(music with { PlaybackOrder = MediaPlaybackOrder.Shuffle }, MediaPlaybackOrder.RepeatTrack,
            value => { requests.Add("shuffle"); current = false; return Task.FromResult(true); },
            value => { requests.Add("repeat"); return Task.FromResult(true); }, () => current);
        Check(!accepted && requests.SequenceEqual(new[] { "shuffle" }), "切换中媒体会话失效时不继续控制新会话");
        requests.Clear();
        accepted = await MediaPlaybackOrders.ChangeAsync(music with { PlaybackOrderKnown = false }, MediaPlaybackOrder.Shuffle,
            value => { requests.Add("shuffle"); return Task.FromResult(true); },
            value => { requests.Add("repeat"); return Task.FromResult(false); }, () => true);
        Check(!accepted && requests.SequenceEqual(new[] { "repeat" }), "播放器拒绝命令时不报告成功或继续发送");
        requests.Clear();
        Check(await Switch(observed, MediaPlaybackOrder.Sequential)
            && requests.SequenceEqual(new[] { "shuffle:False", "repeat:None" }), "原生冲突组合退出时完整关闭随机和单曲");
    }
}
