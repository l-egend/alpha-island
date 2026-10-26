using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private static void LyricRestPolicyChecks()
    {
        var lyrics = LrcParser.Parse("[00:12.00]第一句\n[00:20.00]第二句\n[00:25.00]\n[00:40.00]第三句", null);
        Check(LyricRestTimeline.At(lyrics, TimeSpan.Zero, TimeSpan.FromSeconds(80)) is { NextIndex: 0, EndSeconds: 12 }, "首次演唱前显示前奏并预告第一句歌词");
        Check(LyricRestTimeline.At(lyrics, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(80)) == null, "首句时间戳到达时结束前奏");
        Check(LyricRestTimeline.At(lyrics, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(80)) is { PreviousIndex: 1, NextIndex: 3 }, "曲中空白时间戳标识间奏");
        Check(LyricRestTimeline.At(lyrics, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(80)) == null, "间奏结束时切入下一句");
        Check(LyricRestTimeline.At(lyrics, TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(80)) == null, "不把两句间隔较长直接判断为间奏");
        var words = new LyricsDocument(new[] {
            new LyricLine { Time = TimeSpan.Zero, Text = "第一句", WordEnds = [1, 2, 3], WordChars = [1, 1, 1] },
            new LyricLine { Time = TimeSpan.FromSeconds(15), Text = "下一句" }
        }, true, false, "逐字歌词");
        Check(LyricRestTimeline.At(words, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30)) is { NextIndex: 1, StartSeconds: 3.6 }, "逐字演唱结束且下一句尚远时识别休息段");
        Check(LyricRestTimeline.At(words, TimeSpan.FromSeconds(3.2), TimeSpan.FromSeconds(30)) == null, "唱完后短暂停留，防止圆点闪现");
        var sustained = new LyricsDocument(new[] {
            new LyricLine { Time = TimeSpan.Zero, Text = "长音", WordEnds = [14], WordChars = [2] },
            new LyricLine { Time = TimeSpan.FromSeconds(15), Text = "下一句" }
        }, true, false, "长音");
        Check(LyricRestTimeline.At(sustained, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)) == null, "长音尚未结束时保留歌词");
        var marked = LrcParser.Parse("[00:00.00]第一句\n[00:08.00]（间奏）\n[00:18.00]下一句\n[00:25.00]♪", null);
        Check(LyricRestTimeline.At(marked, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35)) is { NextIndex: 2 }, "识别明确的间奏文字标记");
        Check(LyricRestTimeline.At(marked, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35)) is { NextIndex: -1 }, "尾奏标记使用圆点");
        Check(LyricRestTimeline.At(marked, TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(35)) == null, "歌曲结束后停止休息提示");
        Check(LyricRestTimeline.At(new LyricsDocument(lyrics.Lines, false, false, "纯文本"), TimeSpan.Zero, TimeSpan.FromSeconds(80)) == null, "没有可靠时间轴时不猜测演唱时段");
        Check(LyricRestTimeline.At(null, TimeSpan.Zero, TimeSpan.Zero) == null && LyricRestTimeline.At(lyrics, TimeSpan.FromSeconds(-1), TimeSpan.Zero) == null, "无歌词或无效进度不显示圆点");
        Check(!LyricRestTimeline.IsRestMarker("我的前奏已经开始") && LyricRestTimeline.IsRestMarker("[Instrumental]"), "间奏词只作为独立标记识别");
        Check(LyricRestTimeline.At(LrcParser.Parse("[00:00.80]短前奏", null), TimeSpan.Zero, TimeSpan.FromSeconds(30)) == null, "短于 1.5 秒的空隙不会闪烁提示");
    }

    private static void LyricRestUiChecks(alpha_island.MainWindow window, Grid root)
    {
        const string previews = "bin/LyricRest176/previews"; Directory.CreateDirectory(previews);
        var options = AgentField<IslandOptions>(window, "_options");
        options.LyricsEnabled = true; options.ShowTranslation = true; options.UpNextEnabled = false;
        options.PlayNotifyEnabled = false; options.PauseNotifyEnabled = false;
        using var media = new SmtcService(Dispatcher.CurrentDispatcher); SetAgentField(window, "_smtc", media);
        var track = new NowPlayingInfo { Title = "间奏演示", Artist = "Alpha Island", State = PlaybackState.Playing,
            Duration = TimeSpan.FromSeconds(80), SourceApp = "CloudMusic.exe", ContentKind = MediaContentKind.Music };
        var lyrics = LrcParser.Parse("[00:12.00]第一句歌词\n[00:20.00]第二句歌词\n[00:25.00]\n[00:40.00]间奏后的歌词", "[00:12.00]第一句译文");
        try
        {
            void Seek(double seconds, bool playing = true)
            {
                media.ObserveTransportState(playing ? PlaybackState.Playing : PlaybackState.Paused, true);
                media.UpdateTrack(track with { Position = TimeSpan.FromSeconds(seconds), State = playing ? PlaybackState.Playing : PlaybackState.Paused }, "rest-demo-" + seconds + "-" + playing, null);
                PrivateCall(window, "RefreshLyricPosition"); root.UpdateLayout();
            }
            Seek(2); PrivateCall(window, "ApplyMedia", media.Current!); PrivateCall(window, "ApplyLyrics", lyrics);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music); PumpAnimations(50); root.UpdateLayout();
            Check(window.CardBeatDots.Visibility == Visibility.Visible && window.LyricCurrentBright.Visibility == Visibility.Collapsed
                && window.LyricNext.Text == "第一句歌词" && window.LyricTranslation.Visibility == Visibility.Collapsed, "音乐卡片前奏显示圆点和待唱歌词");
            SaveThemePreview(window.IslandBody, previews + "/intro-card.png");
            Seek(13);
            Check(window.CardBeatDots.Visibility == Visibility.Collapsed && window.LyricCurrentBright.Text == "第一句歌词" && window.LyricTranslation.Text == "第一句译文", "演唱开始后恢复歌词与翻译");
            Seek(30);
            Check(window.CardBeatDots.Visibility == Visibility.Visible && window.LyricPrev.Text == "第二句歌词" && window.LyricNext.Text == "间奏后的歌词", "曲中间奏显示圆点与前后两句");
            SaveThemePreview(window.IslandBody, previews + "/interlude-card.png");
            Seek(41); Check(window.CardBeatDots.Visibility == Visibility.Collapsed && window.LyricCurrentBright.Text == "间奏后的歌词", "拖动到间奏后恢复当前歌词");
            Seek(30, false); PrivateCall(window, "SyncVisualizers");
            Check(window.CardBeatDots.Visibility == Visibility.Visible && !window.CardBeatDots.IsMoving, "暂停时圆点保持静止");
            Seek(13, false); PrivateCall(window, "ProgressTimer_Tick", null, EventArgs.Empty);
            Check(window.CardBeatDots.Visibility == Visibility.Collapsed && window.LyricCurrentBright.Text == "第一句歌词", "暂停时拖动进度也更新显示");
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            Seek(30); PrivateCall(window, "SyncVisualizers");
            Check(window.CardBeatDots.IsMoving && !window.PillBeatDots.IsMoving, "展开卡片只驱动卡片的圆点");
            double[] peakTime = new double[4], peakHeight = [0, 0, 0, 0];
            for (int frame = 0; frame < 60; frame++)
            {
                window.CardBeatDots.SetFrame(TimeSpan.FromSeconds(frame / 30d), true, 1);
                for (int dot = 0; dot < 4; dot++)
                    if (window.CardBeatDots.DotPose(dot).Offset < peakHeight[dot]) { peakHeight[dot] = window.CardBeatDots.DotPose(dot).Offset; peakTime[dot] = frame / 30d; }
            }
            Check(peakTime[0] >= .4 && Enumerable.Range(1, 3).All(i => peakTime[i] - peakTime[i - 1] >= .25), "四个圆点从左到右依次起落，相邻峰值间隔至少 250ms");
            bool bounded = true, moved = false, lifted = false; int waves = 0;
            for (int frame = 0; frame < 135; frame++)
            {
                window.CardBeatDots.SetFrame(TimeSpan.FromSeconds(frame / 30d), true, 1);
                var pose = window.CardBeatDots.DotPose(0); bounded &= pose.Offset is >= -2.16 and <= 0 && pose.Opacity is >= .55 and <= .87; moved |= pose.Offset < -1;
                bool raised = pose.Offset < -1; if (raised && !lifted) waves++; lifted = raised;
            }
            Check(bounded && moved && waves == 2, "律动降低幅度与亮度波动，4.5 秒内只完成两轮轻缓起落");
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(900); root.UpdateLayout(); PrivateCall(window, "RefreshLyricPosition");
            Check(window.PillBeatDots.Visibility == Visibility.Visible && window.PillLyricStage.Visibility == Visibility.Collapsed
                && window.PillLyricGhost.Visibility == Visibility.Collapsed && !window.CardBeatDots.IsMoving, "一级音乐岛使用圆点，隐藏卡片停止运动");
            SaveThemePreview(window.IslandBody, previews + "/interlude-pill.png");
            SetAgentField(window, "_upNextShowing", true); SetAgentField(window, "_upNextUntil", DateTime.UtcNow.AddSeconds(2));
            SetAgentField(window, "_upNextTitle", "临时播放提示"); PrivateCall(window, "SyncUpNextContent");
            Check(window.PillBeatDots.Visibility == Visibility.Collapsed && window.PillLyricStage.Visibility == Visibility.Visible && window.PillLyric.Text == "临时播放提示", "播放提示占用一级音乐岛时不被间奏圆点覆盖");
            SetAgentField(window, "_upNextShowing", false); SetAgentField(window, "_upNextUntil", DateTime.MinValue); PrivateCall(window, "SyncUpNextContent");
            Check(window.PillBeatDots.Visibility == Visibility.Visible && window.PillLyricStage.Visibility == Visibility.Collapsed, "播放提示结束后恢复间奏圆点");
            MotionPolicy.Apply(true, true); PrivateCall(window, "SyncVisualizers");
            Check(!window.PillBeatDots.IsMoving && !window.CardBeatDots.IsMoving && window.PillBeatDots.Visibility == Visibility.Visible, "削减动效时保留静态圆点");
            PrivateCall(window, "SetState", IslandState.Bar, true);
            Check(!window.PillBeatDots.IsMoving && !window.CardBeatDots.IsMoving, "收起状态不驱动圆点");
            PrivateCall(window, "ApplyLyrics", (object?)null);
            Check(window.PillBeatDots.Visibility == Visibility.Collapsed && window.CardBeatDots.Visibility == Visibility.Collapsed
                && window.PillLyricStage.Visibility == Visibility.Visible, "换曲清除歌词时移除圆点并恢复文字层");
        }
        finally { PrivateCall(window, "StopKaraoke"); SetAgentField(window, "_smtc", null); MotionPolicy.Apply(true, false); }
    }
}
