using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.AgentTools;
using AlphaIsland.PluginSdk;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void PersonalToolsUiChecks(alpha_island.MainWindow window,Grid root)
    {
        static async Task<bool> Complete(Task task) { await task; return true; }
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        string folder=Path.Combine(Path.GetTempPath(),"alpha-personal-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var copies=new List<string>();var outputs=new List<ScreenToolOutput>();
            var screen=new ScreenToolsWindow(()=>ChatConfig.From(new IslandOptions()),copyText:copies.Add,completed:outputs.Add);
            try
            {
                screen.Show();screen.ApplyRecognizedText("原始识别文字",true);
                Check(copies.Single()=="原始识别文字"&&outputs.Single().Kind=="ocr","识字并复制复制原文，不依赖译文或 API");
                AgentField<TextBox>(screen,"_result").Text="translated result";
                Descendants<Button>(screen).Single(button=>button.Content as string=="复制原文").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(copies.Last()=="原始识别文字","复制原文入口不会误复制右侧译文");
                screen.ApplyRecognizedText("",true);Check(copies.Count==2,"没有识别结果时不清空或改变剪贴板");
                screen.ApplyPickedColor(new(255,0,0));Check(copies.Last()=="#FF0000","实际取色结果默认复制 HEX");
                Descendants<Button>(screen).Single(button=>button.Content as string=="复制 HSL").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(copies.Last()=="hsl(0, 100%, 50%)","窗口可切换复制 HSL");
                for(int i=0;i<12;i++)screen.ApplyPickedColor(new((byte)i,80,120));
                Check(AgentField<WrapPanel>(screen,"_palette").Children.Count==8,"最近颜色有八项上限，不保留截图历史");
                SaveConversationPreview((FrameworkElement)screen.Content,"bin/personal-tools-previews126/screen-tools.png",640,460);
            }
            finally{screen.Close();}
            using(var left=new System.Drawing.Bitmap(100,80))using(var right=new System.Drawing.Bitmap(100,80))
            {
                using(var graphics=System.Drawing.Graphics.FromImage(left))graphics.Clear(System.Drawing.Color.Red);
                using(var graphics=System.Drawing.Graphics.FromImage(right))graphics.Clear(System.Drawing.Color.Blue);
                var snapshots=new[]{(new System.Drawing.Rectangle(-100,0,100,80),left),(new System.Drawing.Rectangle(0,0,100,80),right)};
                Check(ScreenPixelPicker.ReadAt(snapshots,new(-1,20))==new ScreenColor(255,0,0)
                    &&ScreenPixelPicker.ReadAt(snapshots,new(0,20))==new ScreenColor(0,0,255),"冻结截图按物理负坐标正确选择左右显示器像素");
                Check(ScreenPixelPicker.ReadAt(snapshots,new(300,20))==null,"显示器间隙不读取越界像素");
            }
            using(var bitmap=new System.Drawing.Bitmap(820,180))
            {
                using(var graphics=System.Drawing.Graphics.FromImage(bitmap))using(var font=new System.Drawing.Font("Segoe UI",44,System.Drawing.FontStyle.Bold))
                {graphics.Clear(System.Drawing.Color.White);graphics.DrawString("ALPHA ISLAND 126",font,System.Drawing.Brushes.Black,15,35);}
                string text=AwaitPlugin(new ScreenTextService().ReadBitmapAsync(bitmap,CancellationToken.None));
                Check(text.Contains("ALPHA",StringComparison.OrdinalIgnoreCase),"实际 Windows OCR 离线识别已知夹具，不使用外部模型");
            }
            var reminders=AgentField<UserReminderService>(window,"_userReminders");
            PrivateCall(window,"OpenAssistantCenter","reminders");
            var center=AgentField<AssistantCenterWindow>(window,"_assistantCenter");
            Check(center.IsVisible,"管理提醒窗口由实际主窗口入口打开");
            AgentField<TextBox>(center,"_input").Text="30分钟后提醒我UI管理提醒";
            Descendants<Button>(center).Single(button=>button.Content as string=="添加提醒").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reminders.Jobs.Any(job=>job.Title=="UI管理提醒"),"管理界面实际创建任务，无需 API");
            center.SelectSection("habits");center.UpdateLayout();Check(AgentField<TextBlock>(center,"_learning").Text.Contains("已观察")&&AgentField<TextBlock>(center,"_learning").IsVisible,"习惯页显示真实冷启动状态，不伪造建议");
            SaveThemePreview((FrameworkElement)center.Content,"bin/personal-tools-previews126/habits-cold.png");
            center.SelectSection("health");center.UpdateLayout();Check(AgentField<TextBlock>(center,"_health").Text.Contains("基线样本")&&AgentField<TextBlock>(center,"_health").IsVisible,"健康页可展示基线与数据状态");
            SaveThemePreview((FrameworkElement)center.Content,"bin/personal-tools-previews126/health.png");
            using(var settings=new ClipboardSettingsLifetime())
            {
                settings.Window.LoadFrom(new IslandOptions().Normalized());
                Check(new[]{"NaturalRemindersEnabledToggle","HabitLearningEnabledToggle","SystemHealthEnabledToggle","ScreenToolsHotkeysEnabledToggle"}.All(name=>settings.Window.FindName(name) is System.Windows.Controls.CheckBox),"四项功能控制实际呈现在智能助手设置中");
                Check(settings.Window.FindName("ScreenOcrShortcutBox") is TextBox&&settings.Window.FindName("ScreenColorShortcutBox") is TextBox,"识字与取色的快捷键可在设置中修改");
            }
            var registry=AgentField<ToolRegistry>(window,"_desktopTools");
            var chatOptions=new IslandOptions();SettingsRegistry.SetExtra(chatOptions,"AiEnabled",false);
            var context=new ConversationPluginFixture(chatOptions,folder){ToolDispatcher=registry};
            using(var chat=new AlphaIsland.Plugins.ApiChat.ApiChatView(context))
            {
                int attempted=0;
                SetAgentField(chat,"_aiConfigFactory",new Func<alpha_island.Conversations.IChatSettings,ChatConfig>(_=>{attempted++;throw new Exception("Network config must not be read");}));
                AwaitPlugin(Complete((Task)PrivateCall(chat,"SendAiAsync","2分钟后提醒我本地对话提醒")!));
                Check(attempted==0&&reminders.Jobs.Any(job=>job.Title=="本地对话提醒"),"未启用 API 时明确提醒走真实工具桥，在读取网络配置之前完成");
                var messages=AgentField<List<ChatMessage>>(chat,"_aiMessages");
                Check(messages.TakeLast(2).All(message=>message.LocalOnly),"本地提醒消息带本地历史标记");
                var sessions=AgentField<ChatSessionStore>(chat,"_chatSessions");
                Check(sessions.Load(AgentField<string>(chat,"_aiSessionId")).TakeLast(2).All(message=>message.LocalOnly),"保存再读取对话历史仍保留本地标记，不因重启变成上传内容");
                var request=(List<ChatMessage>)PrivateCall(chat,"BuildAiRequest",ChatConfig.From(new IslandOptions()))!;
                Check(request.All(message=>!message.Content.Contains("本地对话提醒")),"后续 API 请求不会附带本地提醒的内容");
                AwaitPlugin(Complete((Task)PrivateCall(chat,"SendAiAsync","取消本地对话提醒的提醒")!));
                Check(!reminders.Jobs.Any(job=>job.Active&&job.Title=="本地对话提醒"),"自然语言取消通过精确事项匹配实际生效");
            }
            reminders.Create("1秒后提醒我锁屏错过测试");
            WriteWindowMember(window,"_sessionLocked",true);PumpAnimations(1300);reminders.TickNow();PumpAnimations(50);
            var inbox=AgentField<DesktopNotificationInbox>(window,"_desktopInbox");
            Check(inbox.History.Any(item=>item.Title=="锁屏错过测试"&&item.IsPinned&&item.Reminder?.Rule=="user-reminder"),"锁屏到期提醒进入通知中心并置顶");
            Check(inbox.PendingCount==0,"锁屏期间不弹出提醒或强行展开岛");
            WriteWindowMember(window,"_sessionLocked",false);
            var assistantRules=AgentField<AssistantReminderService>(window,"_assistantReminders");
            assistantRules.Configure(new AssistantRuleOptions{Enabled=false,Microphone=false});assistantRules.TickNow();
            Check(inbox.History.Any(item=>item.Title=="锁屏错过测试"&&item.Reminder is{Pending:true}),"关闭主动建议不会把用户自己创建的待办提醒误判为过期");
            var insights=AgentField<DesktopInsightsService>(window,"_insights");
            var learner=AgentField<HabitLearner>(insights,"_habits");
            var today=DateTimeOffset.Now;
            bool weekday=today.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday);
            var days=Enumerable.Range(1,27).Select(value=>today.AddDays(-value)).Where(value=>(value.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday))==weekday).Take(5)
                .Select(value=>new HabitDay(value.ToString("yyyy-MM-dd"),570)).ToArray();
            learner.Restore(new(days,[],new(),[],new()),today);center.SelectSection("habits");center.UpdateLayout();
            SaveThemePreview((FrameworkElement)center.Content,"bin/personal-tools-previews126/habit-suggestion.png");
            Descendants<Button>(center).Single(button=>button.Content as string=="采纳建议").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(reminders.Jobs.Any(job=>job.Active&&job.Title=="开始专注"&&job.Weekdays.Length==(weekday?5:2)),"采纳真实学习建议后创建工作日或周末提醒，不自动启动专注");
            Check(insights.Suggestions.All(suggestion=>!suggestion.Id.StartsWith("work:")),"采纳反馈保存在学习状态中，不重复提出同一配置");
            center.SelectSection("reminders");center.UpdateLayout();SaveThemePreview((FrameworkElement)center.Content,"bin/personal-tools-previews126/reminders.png");
            center.Close();
        }
        finally
        {
            WriteWindowMember(window,"_sessionLocked",false);
            AgentField<AssistantCenterWindow?>(window,"_assistantCenter")?.Close();
            SynchronizationContext.SetSynchronizationContext(previous);Directory.Delete(folder,true);
        }
    }
}
