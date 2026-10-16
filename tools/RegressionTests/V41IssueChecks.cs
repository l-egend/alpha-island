using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Point = System.Windows.Point;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void V41IssueChecks(alpha_island.MainWindow window, Grid root)
    {
        var failures = new List<string>();
        void Repro(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.GetBaseException().Message); Console.WriteLine("FAIL: " + name + ": " + ex.GetBaseException().Message); }
        }
        Repro("notification original text survives ingestion and full reader", () =>
        {
            string title = new string('题', 330) + "标题末尾";
            string body = string.Join("\n", Enumerable.Range(1, 80).Select(i => "正文第" + i + "行：" + new string('文', 80))) + "正文末尾";
            var notification = SystemToastService.CreateNotification("v41-text", "AlphaIsland.Test", "Test",
                [title, body], DateTimeOffset.UtcNow, 42, null);
            var card = new alpha_island.Controls.DesktopNotificationCard(notification);
            card.SetFullContentExpanded(true);
            var reader = FullContentDescendants(card).OfType<System.Windows.Controls.TextBox>().Single();
            Check(reader.Text.Contains("标题末尾") && reader.Text.EndsWith("正文末尾"),
                "读取阶段不能截断标题或正文，全文阅读必须包含通知原文末尾");
        });
        Repro("agent minimizes to the main island center from any card", () =>
        {
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            root.UpdateLayout();
            var landing = (Rect)PrivateCall(window, "AgentIslandScreenRect", true)!;
            var center = root.PointToScreen(new Point(root.ActualWidth / 2, 0));
            Check(Math.Abs(landing.X + landing.Width / 2 - center.X) < 2,
                "助手收回窗口时的落点始终位于主岛正中心");
        });
        Repro("selected card hold expires when game consumes outside input", () =>
        {
            SetAgentField(window, "_componentCardAwaitingPointer", true);
            SetAgentField(window, "_componentSelectedTick", Environment.TickCount64 - 3000);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            bool held = (bool)PrivateCall(window, "HoldSelectedComponentCard", false)!;
            Check(!held && !AgentField<bool>(window, "_componentCardAwaitingPointer"),
                "光标没有再进入岛也不能无限保持选页卡片，游戏吞掉点击后仍可正常收回");
        });
        Repro("stale polling cannot immediately undo a native pause event", () =>
        {
            using var smtc = new SmtcService(Dispatcher.CurrentDispatcher) { AudioProbeEnabled = true };
            smtc.ObserveTransportState(PlaybackState.Playing, true);
            smtc.UpdateTrack(new NowPlayingInfo { Title = "Event fixture", SourceApp = "cloudmusic.exe", SessionId = "fixture",
                ContentKind = MediaContentKind.Music, State = PlaybackState.Playing, Position = TimeSpan.FromSeconds(30),
                Duration = TimeSpan.FromMinutes(3) }, "fixture-track", null);
            smtc.ObserveTransportState(PlaybackState.Paused, true); PrivateCall(smtc, "PublishResolvedState");
            smtc.ObserveTransportState(PlaybackState.Playing, false); PrivateCall(smtc, "PublishResolvedState");
            Check(smtc.Current?.State == PlaybackState.Paused,
                "新的暂停事件不被紧接着的旧播放轮询反向覆盖");
        });
        Repro("lyrics reject a different singer even with identical title and duration", () =>
        {
            using var service = new LyricsService();
            var score = (double)typeof(LyricsService).GetMethod("EvaluateCandidateScore", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["同名歌曲", "同名歌曲", "原唱甲", TimeSpan.FromSeconds(180), "同名歌曲", "翻唱乙", 180000L, "", ""])!;
            Check(score < service.MinScore, "歌名和时长相同不能抵消歌手完全不匹配，拒绝错误歌词版本");
        });
        if (failures.Count == 0) V41FeatureChecks(window, root);
        SetAgentField(window, "_componentCardAwaitingPointer", false);
        PrivateCall(window, "SetState", IslandState.Bar, true);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
}
