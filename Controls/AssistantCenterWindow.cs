using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using FontFamily=System.Windows.Media.FontFamily;

namespace alpha_island.Controls;

/// <summary>Management UI only; scheduling, learning and trend state remain in their own modules.</summary>
internal sealed class AssistantCenterWindow:Window
{
    private readonly UserReminderService _reminders;
    private readonly DesktopInsightsService _insights;
    private readonly Func<string,ToolResult> _adopt;
    private readonly TextBox _input=new(){MinWidth=250,MaxLength=512,Padding=new Thickness(9),ToolTip=L.T("例如：30分钟后提醒我喝水；每个工作日9点提醒我专注")};
    private readonly StackPanel _reminderRows=new(),_habitRows=new(),_healthRows=new();
    private readonly StackPanel _reminderPage=new(),_habitPage=new(),_healthPage=new();
    private readonly TextBlock _status=Text(L.T("提醒在本机保存；程序需保持运行，恢复运行后会补记错过事项。"),11);
    private readonly TextBlock _count=Text("",12),_learning=Text("",12),_health=Text("",12);
    private readonly System.Windows.Controls.CheckBox _completed=new(){Content=L.T("显示最近已提醒"),Margin=new Thickness(0,8,0,8)};
    private readonly Dictionary<string,ScrollViewer> _sections=new();
    private string _section="reminders";
    internal AssistantCenterWindow(UserReminderService reminders,DesktopInsightsService insights,Func<string,ToolResult> adopt)
    {
        _reminders=reminders;_insights=insights;_adopt=adopt;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml")});
        Title=L.T("提醒与本地分析");Width=680;Height=560;MinWidth=550;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=new SolidColorBrush(Color.FromRgb(24,26,32));SetResourceReference(ForegroundProperty,"TextPrimary");
        FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root=new Grid{Margin=new Thickness(20)};
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var navigation=new WrapPanel{Margin=new Thickness(0,0,0,14)};
        foreach(var (id,title) in new[]{("reminders",L.T("我的提醒")),("habits",L.T("习惯学习")),("health",L.T("系统健康"))})
            navigation.Children.Add(Action(title,()=>SelectSection(id)));
        root.Children.Add(navigation);
        var body=new Grid();Grid.SetRow(body,1);root.Children.Add(body);
        foreach(var (id,panel) in new[]{("reminders",_reminderPage),("habits",_habitPage),("health",_healthPage)})
        {var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_sections.Add(id,scroll);body.Children.Add(scroll);}
        var entry=new Grid{Margin=new Thickness(0,0,0,9)};entry.ColumnDefinitions.Add(new());entry.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        entry.Children.Add(_input);var add=Action(L.T("添加提醒"),CreateReminder);Grid.SetColumn(add,1);entry.Children.Add(add);
        _input.KeyDown+=(_,args)=>{if(args.Key==System.Windows.Input.Key.Enter){CreateReminder();args.Handled=true;}};
        _reminderPage.Children.Add(Text(L.T("支持明确时间、每天、工作日、每周及间隔重复；含糊时间会要求补充。"),12));
        _reminderPage.Children.Add(entry);_reminderPage.Children.Add(_count);_reminderPage.Children.Add(_completed);_reminderPage.Children.Add(_reminderRows);
        _completed.Checked+=(_,_)=>Refresh();_completed.Unchecked+=(_,_)=>Refresh();
        _habitPage.Children.Add(_learning);
        _habitPage.Children.Add(Text(L.T("只保留28天的类别时段和有效时长，不记录窗口标题、输入内容或截图；15分钟时间桶，样本不足不推测。采纳后才更改设置。"),12));
        _habitPage.Children.Add(Action(L.T("清除学习记录"),()=>{_insights.ClearLearning();_status.Text=L.T("学习记录及建议反馈已清空。");}));
        _habitPage.Children.Add(_habitRows);
        _healthPage.Children.Add(_health);
        _healthPage.Children.Add(Text(L.T("高负载需持续观察；进程私有内存增长只表示趋势，不等于泄漏。详情采样在后台进行，锁屏或游戏时暂停。"),12));
        var healthActions=new WrapPanel();healthActions.Children.Add(Action(L.T("刷新详情"),()=>_insights.TickNow()));
        healthActions.Children.Add(Action(L.T("重新建立基线"),()=>{_insights.ResetBaseline();_status.Text=L.T("已清除旧基线，将从新的有效采样学习。");}));
        healthActions.Children.Add(Action(L.T("任务管理器"),OpenTaskManager));_healthPage.Children.Add(healthActions);_healthPage.Children.Add(_healthRows);
        _status.Margin=new Thickness(0,12,0,0);Grid.SetRow(_status,2);root.Children.Add(_status);Content=root;
        _reminders.Changed+=RefreshIfVisible;_insights.Changed+=RefreshIfVisible;
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
        Closed+=(_,_)=>{_reminders.Changed-=RefreshIfVisible;_insights.Changed-=RefreshIfVisible;};
        PreviewKeyDown+=(_,args)=>{if(args.Key==System.Windows.Input.Key.Escape){Hide();args.Handled=true;}};
        SelectSection("reminders");
    }
    internal void SelectSection(string section)
    {
        _section=section is "habits" or "health"?section:"reminders";
        foreach(var pair in _sections)pair.Value.Visibility=pair.Key==_section?Visibility.Visible:Visibility.Collapsed;
        Refresh();
    }
    private void RefreshIfVisible(){if(IsVisible)Refresh();}
    private void Refresh()
    {
        if(_section=="reminders")
        {
            _input.IsEnabled=_reminders.Enabled;
            var jobs=_reminders.Jobs;
            _count.Text=L.F($"待办 {jobs.Count(job=>job.Active)} / 100")+(_reminders.Enabled?"":L.T(" · 提醒已暂停，可在智能助手设置中恢复"));
            _reminderRows.Children.Clear();
            foreach(var job in jobs.Where(job=>job.Active||_completed.IsChecked==true))
            {
                string date=TimeZoneInfo.ConvertTime(job.Due,TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss");
                var row=new StackPanel();row.Children.Add(Text(job.Title,14));row.Children.Add(Text(date+(job.Active?L.T(" · 待提醒"):L.T(" · 已提醒"))+(job.Weekdays.Length>0||job.IntervalMinutes>0?L.T(" · 重复"):""),11));
                var actions=new WrapPanel();
                if(!job.Active)actions.Children.Add(Action(L.T("10分钟后再提醒"),()=>Show(_reminders.Snooze(job.Id,10))));
                actions.Children.Add(Action(job.Active?L.T("取消提醒"):L.T("删除记录"),()=>Show(_reminders.Cancel(job.Id))));row.Children.Add(actions);
                _reminderRows.Children.Add(Card(row));
            }
            if(_reminderRows.Children.Count==0)_reminderRows.Children.Add(Text(L.T("暂无提醒。可在上方或 API 对话里输入：30分钟后提醒我喝水。"),12));
        }
        else if(_section=="habits")
        {
            _learning.Text=_insights.LearningStatus;_habitRows.Children.Clear();
            foreach(var suggestion in _insights.Suggestions)
            {
                var row=new StackPanel();row.Children.Add(Text(suggestion.Title,14));row.Children.Add(Text(suggestion.Detail,12));
                var actions=new WrapPanel();actions.Children.Add(Action(L.T("采纳建议"),()=>Show(_adopt(suggestion.Id))));
                actions.Children.Add(Action(L.T("暂不采纳"),()=>{_insights.Respond(suggestion.Id,false);_status.Text=L.T("这条建议14天内不再出现。");}));row.Children.Add(actions);_habitRows.Children.Add(Card(row));
            }
            if(_habitRows.Children.Count==0)_habitRows.Children.Add(Text(L.T("暂无足够稳定的新规律。正常使用后再查看；已采纳或暂不采纳的建议不会反复打扰。"),12));
        }
        else
        {
            _health.Text=_insights.HealthStatus;_healthRows.Children.Clear();
            _healthRows.Children.Add(Text(L.T("当前进程 · CPU / 私有内存"),13));
            foreach(var sample in _insights.Processes)
                _healthRows.Children.Add(Card(Text($"{sample.Name}    {sample.Cpu?.ToString("0.#")??"—"}%    {sample.PrivateBytes/1048576d:0} MB",12)));
            if(_insights.Processes.Length==0)_healthRows.Children.Add(Text(L.T("暂无有效进程样本。可点击刷新；部分受保护进程不能读取，缺失数据不会显示为零。"),12));
            _healthRows.Children.Add(Text(L.T("最近预警"),13));
            foreach(var alert in _insights.Alerts)
            {var row=new StackPanel();row.Children.Add(Text(alert.At.ToLocalTime().ToString("MM-dd HH:mm")+" · "+alert.Title,13));row.Children.Add(Text(alert.Detail,12));_healthRows.Children.Add(Card(row));}
            if(_insights.Alerts.Length==0)_healthRows.Children.Add(Text(L.T("尚未发现满足持续时间与阈值条件的预警。"),12));
        }
    }
    private void CreateReminder()
    {var result=_reminders.Create(_input.Text);Show(result);if(result.Success)_input.Clear();}
    private void Show(ToolResult result){_status.Text=result.Message;Refresh();}
    private void OpenTaskManager()
    {try{Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"Taskmgr.exe")){UseShellExecute=true});}catch(Exception){_status.Text=L.T("任务管理器未能打开，可按 Ctrl+Shift+Esc。");}}
    private static TextBlock Text(string value,double size)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,8)};
    private static Border Card(UIElement child)=>new(){Child=child,Padding=new Thickness(13,10,13,10),CornerRadius=new CornerRadius(10),Margin=new Thickness(0,0,0,8),Background=new SolidColorBrush(Color.FromArgb(18,255,255,255))};
    private static Button Action(string title,Action action)
    {var button=new Button{Content=title,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,3,8,3)};button.Click+=(_,args)=>{args.Handled=true;action();};return button;}
}
