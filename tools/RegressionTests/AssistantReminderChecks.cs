using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static AssistantFrame ReminderFrame(DateTimeOffset now, long stamp = 1000) => new(now, stamp, false, false, false,
        null, 0, true, false, [], "", false, 60, true, [], [], [], "", null);
    private static DesktopNotification[] Notices(AssistantReminderChange[] changes) => changes.Where(c => c.Notice != null).Select(c => c.Notice!).ToArray();
    private static void AssistantReminderChecks()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var options = new AssistantRuleOptions { AgentWaitMinutes = 1, DownloadStallSeconds = 30, UsageBreakMinutes = 15 };
        AssistantReminderEngine Engine() { var result = new AssistantReminderEngine(TimeZoneInfo.Utc); result.Configure(options); return result; }
        var engine = Engine();
        var wait = new AssistantAgent("external:codex/task-a", 41, AgentRunState.ToolWait, "Codex · 会话 1");
        engine.Evaluate(ReminderFrame(now) with { Agents = [wait] });
        Check(Notices(engine.Evaluate(ReminderFrame(now.AddSeconds(59), 60000) with { Agents = [wait] })).Length == 0, "等待授权未达阈值不额外提醒");
        var notice = Notices(engine.Evaluate(ReminderFrame(now.AddSeconds(61), 62000) with { Agents = [wait] })).Single();
        Check(notice.IsPinned && notice.Reminder is { Rule: "agent-wait", Pending: true }, "授权等待过久写入置顶待处理通知");
        Check(Notices(engine.Evaluate(ReminderFrame(now.AddSeconds(65), 66000) with { Agents = [wait] })).Length == 0, "状态续期不重复生成等待提醒");
        Check(engine.Evaluate(ReminderFrame(now.AddSeconds(66), 67000) with { Agents = [wait with { State = AgentRunState.Working }] }).Any(c => c.ResolveKey == notice.Key), "离开等待状态会自动解除旧事项");
        Check(Notices(engine.Evaluate(ReminderFrame(now.AddSeconds(67), 68000) with { Agents = [wait with { State = AgentRunState.Error }] })).Any(n => n.Reminder?.Rule == "agent-error"), "真实错误状态有独立处理入口");
        var inputEngine = Engine(); var inputWait = wait with { State = AgentRunState.InputWait };
        inputEngine.Evaluate(ReminderFrame(now) with { Agents = [inputWait] });
        var inputNotice = Notices(inputEngine.Evaluate(ReminderFrame(now.AddSeconds(61), 62000) with { Agents = [inputWait] })).Single();
        Check(inputNotice.Body.Contains("等待回复") && !inputNotice.Body.Contains("授权"), "等待用户输入的跟进通知不会误写成工具授权");
        Check(Notices(inputEngine.Evaluate(ReminderFrame(now.AddSeconds(65), 66000) with { Agents = [inputWait] })).Length == 0, "等待回复的重复快照不会连续堆叠通知");
        Check(inputEngine.Evaluate(ReminderFrame(now.AddSeconds(66), 67000) with { Agents = [inputWait with { State = AgentRunState.Thinking }] }).Any(c => c.ResolveKey == inputNotice.Key), "回复后自动解除仍需处理的等待通知");
        var completion = Engine(); completion.Evaluate(ReminderFrame(now) with { Agents = [wait with { State = AgentRunState.Working }] });
        completion.RecordEnded(wait.Owner, 41, now.AddSeconds(1), false); completion.RecordEnded(wait.Owner, 41, now.AddSeconds(1), false);
        var ended = Notices(completion.Evaluate(ReminderFrame(now.AddSeconds(5), 6000))).Single();
        Check(ended.Title.StartsWith("1 个") && ended.Reminder?.Actions[0].Target == wait.Owner, "明确结束信号合并并去重，保留对应助手入口");
        var expired = Engine(); expired.Evaluate(ReminderFrame(now) with { Agents = [wait] });
        Check(!Notices(expired.Evaluate(ReminderFrame(now.AddSeconds(4), 5000))).Any(n => n.Reminder?.Rule == "agent-ended"), "活动从快照消失不能推断任务已完成");

        var download = Engine(); var task = new AssistantDownload("file-a", "Edge · 文件.zip", 100, true, false);
        download.Evaluate(ReminderFrame(now) with { Downloads = [task] });
        Check(Notices(download.Evaluate(ReminderFrame(now.AddSeconds(31), 32000) with { Downloads = [task] })).Any(n => n.Reminder?.Rule == "download"), "可信下载进度持续不动达到阈值时提醒");
        Check(download.Evaluate(ReminderFrame(now.AddSeconds(32), 33000) with { Downloads = [task with { Bytes = 110 }] }).Any(c => c.ResolveKey == "assistant:download:file-a"), "进度恢复后自动解除停滞通知");
        var paused = Engine(); paused.Evaluate(ReminderFrame(now) with { Downloads = [task with { Measuring = false }] });
        Check(Notices(paused.Evaluate(ReminderFrame(now.AddSeconds(60), 61000) with { Downloads = [task with { Measuring = false }] })).Length == 0, "暂停、未知总量和非下载阶段均不做停滞判断");

        var game = Engine(); game.Evaluate(ReminderFrame(now) with { Game = "示例游戏", Quiet = true, Agents = [wait with { State = AgentRunState.Working }], Downloads = [task] });
        game.RecordEnded(wait.Owner, 41, now.AddSeconds(1), false);
        game.Evaluate(ReminderFrame(now.AddSeconds(30), 31000) with { Game = "示例游戏", Quiet = true, Downloads = [task with { Measuring = false, Completed = true }] });
        var message = new DesktopNotification("wechat-new", "wechat", "微信", "联系人", "消息", now.AddSeconds(2));
        var wrap = Notices(game.Evaluate(ReminderFrame(now.AddSeconds(60), 61000) with { Notifications = [message], DayGameSeconds = 1200 }));
        var gameCard = wrap.Single(n => n.Reminder?.Rule == "game");
        Check(gameCard.Body.Contains("1 个助手") && gameCard.Body.Contains("1 个下载") && gameCard.Body.Contains("未读 1 条"), "游戏收尾合并实际观察到的任务、下载与保留未读数");
        Check(gameCard.Reminder!.Actions.Any(a => a.Id == "break-five") && !wrap.Any(n => n.Reminder?.Rule == "digest"), "游戏结束使用一张收尾通知，不同时再弹重复摘要");
        var mixed = Engine(); mixed.Evaluate(ReminderFrame(now) with { Quiet = true, Game = "游戏" });
        mixed.Evaluate(ReminderFrame(now.AddSeconds(30), 31000) with { Quiet = true, Notifications = [message] });
        Check(!Notices(mixed.Evaluate(ReminderFrame(now.AddSeconds(31), 32000) with { Notifications = [message] })).Any(n => n.Reminder?.Rule == "digest"), "游戏结束后再关闭手动静默，不重复汇总已经进入收尾卡的消息");
        var quiet = Engine(); quiet.Evaluate(ReminderFrame(now) with { Quiet = true });
        var digest = Notices(quiet.Evaluate(ReminderFrame(now.AddSeconds(5), 6000) with { Notifications = [message] })).Single();
        Check(digest.Reminder?.Rule == "digest" && digest.Body.Contains("未读 1 条"), "手动免打扰结束可独立生成摘要");
        var focus = Engine(); focus.Evaluate(ReminderFrame(now) with { Focus = true });
        Check(Notices(focus.Evaluate(ReminderFrame(now.AddSeconds(5), 6000) with { Notifications = [message] })).Any(n => n.Reminder?.Rule == "digest"), "专注结束同样汇总期间未读");

        var meeting = Engine(); var meetingFrame = ReminderFrame(now) with { DeviceApps = ["Teams"], MediaSession = "player-a", MediaPlaying = true };
        meeting.Evaluate(ReminderFrame(now.AddSeconds(-3), 0) with { MediaSession = "player-a", MediaPlaying = true });
        meeting.Evaluate(meetingFrame);
        var meetingNotice = Notices(meeting.Evaluate(meetingFrame with { Now = now.AddSeconds(7), Stamp = 8000 }));
        Check(meetingNotice.Any(n => n.Reminder?.Rule == "meeting") && !meetingNotice.Any(n => n.Reminder?.Rule == "microphone"), "扬声器静音不会误当成麦克风静音");
        var mic = Notices(meeting.Evaluate(meetingFrame with { Now = now.AddSeconds(8), Stamp = 9000, MicrophoneMuted = true })).Single();
        Check(mic.Reminder?.Rule == "microphone" && mic.Reminder.Actions.All(a => a.Id != "volume-restore"), "会议守卫读取输入静音且不提供自动开麦");
        meeting.Evaluate(ReminderFrame(now.AddSeconds(10), 11000) with { MediaSession = "player-a" });
        var meetingEnd = Notices(meeting.Evaluate(ReminderFrame(now.AddSeconds(31), 32000) with { MediaSession = "player-a" })).Single(n => n.Reminder?.Rule == "meeting-wrap");
        Check(meetingEnd.Reminder!.Volume == 60 && meetingEnd.Reminder.Actions.Any(a => a.Id == "media-resume"), "会议占用结束后提供记录过的会前音量及同一播放器恢复操作");
        var unknown = Engine(); unknown.Evaluate(meetingFrame); unknown.Evaluate(meetingFrame with { Now = now.AddSeconds(7), Stamp = 8000 });
        Check(!Notices(unknown.Evaluate(ReminderFrame(now.AddSeconds(40), 41000) with { PrivacyFresh = false })).Any(n => n.Reminder?.Rule == "meeting-wrap"), "隐私状态读取失败不会被当作会议结束");

        var usage = Engine(); usage.Evaluate(ReminderFrame(now)); DesktopNotification? usageNotice = null;
        for (int i = 1; i <= 16; i++) usageNotice ??= Notices(usage.Evaluate(ReminderFrame(now.AddMinutes(i), 1000 + i * 60000))).FirstOrDefault(n => n.Reminder?.Rule == "usage");
        Check(usageNotice != null, "连续有效输入时长达到设置阈值后建议休息");
        usage.Evaluate(ReminderFrame(now.AddMinutes(17), 1021000) with { IdleSeconds = 600 });
        Check(Notices(usage.Evaluate(ReminderFrame(now.AddMinutes(18), 1081000))).All(n => n.Reminder?.Rule != "usage"), "五分钟闲置清除连续使用时长");
        var snooze = Engine(); snooze.Respond("game", "later", gameCard.Created); snooze.Defer(gameCard, gameCard.Created);
        Check(Notices(snooze.Evaluate(ReminderFrame(gameCard.Created.AddMinutes(9), 541000))).Length == 0, "稍后提醒在十分钟内不重现");
        Check(Notices(snooze.Evaluate(ReminderFrame(gameCard.Created.AddMinutes(10), 601000))).Any(n => n.Key == gameCard.Key && n.IsPinned), "一次性游戏收尾也能真正稍后重现，不只是隐藏");
        var canceled = Engine(); canceled.Respond("game", "later", gameCard.Created); canceled.Defer(gameCard, gameCard.Created); canceled.Respond("game", "today", gameCard.Created);
        var restored = Engine(); restored.Restore(canceled.Snapshot(), gameCard.Created);
        Check(Notices(restored.Evaluate(ReminderFrame(gameCard.Created.AddMinutes(11), 661000))).Length == 0, "今天不再提醒取消待重现事项并跨重启保留");
        var timer = Engine(); timer.Respond("usage", "break-five", now);
        Check(Notices(timer.Evaluate(ReminderFrame(now.AddMinutes(5), 301000))).Any(n => n.Reminder?.Rule == "break"), "五分钟休息计时到期产生本地通知");
        var daily = Engine(); var evening = ReminderFrame(now.AddHours(9)) with { DaySummary = "专注 25 分钟，游戏 30 分钟" };
        var review = Notices(daily.Evaluate(evening)).Single();
        Check(review.Reminder?.Rule == "daily" && review.Reminder.Actions.Any(a => a.Id == "daily-ai"), "设定时段出现本地回顾，AI 整理仅作为可点击动作");
        var recovered = Engine(); recovered.Restore(daily.Snapshot(), evening.Now);
        Check(Notices(recovered.Evaluate(evening with { Now = evening.Now.AddMinutes(1) })).Length == 0, "当天回顾不会因重启再次生成");
        var blocked = Engine(); Check(Notices(blocked.Evaluate(evening with { Locked = true })).Length == 0, "锁屏时不生成可见提醒");
        blocked.Configure(options with { Enabled = false, Microphone = false });
        Check(Notices(blocked.Evaluate(evening)).Length == 0, "关闭本地建议后不再生成回顾");

        var inbox = new DesktopNotificationInbox(); inbox.Add(gameCard, false);
        for (int i = 0; i < 120; i++) inbox.Add(message with { Key = "ordinary-" + i, Created = now.AddSeconds(i) }, false);
        Check(inbox.History.Count == 100 && inbox.History.Any(n => n.Key == gameCard.Key), "普通消息达到容量上限时优先保留置顶待处理事项");
        inbox.Clear(keepPinned: true); Check(inbox.History.Count == 1, "清空未置顶记录不会移除待处理事项");
        inbox.ResolveReminder(gameCard.Key); Check(inbox.History[0].IsRead && !inbox.History[0].IsPinned && inbox.History[0].Reminder?.Pending == false, "处理完成同时解除置顶并保存已读状态");
        var queued = new DesktopNotificationInbox(); var policy = new DesktopNotificationPolicy(new IslandOptions()); queued.Add(notice, true, policy); queued.ResolveReminder(notice.Key);
        Check(queued.PendingCount == 0 && queued.TakeNext(policy, false, now.AddSeconds(62)) == null, "已恢复的等待或错误通知同步退出待展示队列，不能晚些时候再弹旧状态");
        var source = new IslandOptions().Normalized();
        Check(SettingsRegistry.Find("AssistantDailyReviewMinute") != null && AssistantRuleOptions.From(source).DownloadStallSeconds == 90, "全部规则及时间阈值进入现有设置注册表");
        Check(!AssistantReminderRules.Valid(new("game", true, now.AddHours(1), [new("shell", "执行命令", "unsafe")])), "通知元数据只允许固定本地动作，不能注入执行命令");
        int explicitEnds = 0;
        void Count(AgentActivityEnded _) => explicitEnds++;
        AgentStatus.ActivityEnded += Count;
        try
        {
            AgentStatus.Set(AgentRunState.Working, owner: "reminder-expired-test"); AgentStatus.Reset(owner: "reminder-expired-test");
            Check(explicitEnds == 0, "普通 Reset 不发布任务完成事件");
            AgentStatus.Set(AgentRunState.Working, owner: "reminder-explicit-test"); AgentStatus.Finish("reminder-explicit-test"); AgentStatus.Finish("reminder-explicit-test");
            Check(explicitEnds == 1, "明确结束发布一次事件且重复 Stop 不重复计数");
            var registry = new ExternalAgentRegistry();
            registry.Update("codex", "reminder-parent", "working", "", 30, now.UtcDateTime, out _, beginsActivity: true);
            registry.Update("codex", "reminder-child", "working", "", 30, now.UtcDateTime, out _, parentSession: "reminder-parent");
            registry.Update("codex", "reminder-child", "idle", "", 30, now.UtcDateTime.AddSeconds(1), out _, parentSession: "reminder-parent");
            Check(explicitEnds == 1, "附带智能体结束不额外计为一个主会话完成");
            registry.Update("codex", "reminder-parent", "idle", "", 30, now.UtcDateTime.AddSeconds(2), out _);
            Check(explicitEnds == 2, "原生主会话结束只发布一条任务结束信号");
            registry.Update("codex", "reminder-expiring", "thinking", "", 5, now.UtcDateTime, out _, beginsActivity: true);
            registry.Sweep(now.UtcDateTime.AddSeconds(30));
            Check(explicitEnds == 2, "真实外部会话心跳过期不发布完成信号"); registry.Clear();
        }
        finally { AgentStatus.ActivityEnded -= Count; AgentStatus.Reset(owner: "reminder-explicit-test"); }
    }
}
