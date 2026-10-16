using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void ClipboardHistoryPreferencesChecks(alpha_island.MainWindow window,string root)
    {
        string path=Path.Combine(root,"retained-clipboard.json");
        var records=Enumerable.Range(0,30).Select(i=>new ClipboardEntry("history-"+i,DateTime.Now.AddMinutes(-i))).ToArray();
        File.WriteAllText(path,JsonSerializer.Serialize(records));
        uint sequence=40;string payload="initial";var writes=new List<string>();int copies=0,pastes=0;
        using var hotkeys=new HotkeyService(Dispatcher.CurrentDispatcher);
        using var clipboard=new ClipboardService(Dispatcher.CurrentDispatcher,hotkeys,()=>sequence,()=>payload,path,text=>{writes.Add(text);payload=text;sequence++;});
        clipboard.SetHistoryLimit(25);
        typeof(ClipboardService).GetMethod("LoadHistory",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(clipboard,null);
        Check(clipboard.History.Count==25 && clipboard.History[0].Text=="history-0" && clipboard.History[^1].Text=="history-24", "启动加载采用配置条数并保留最新记录，不再固定取前 10 条");
        Check(JsonSerializer.Deserialize<ClipboardEntry[]>(File.ReadAllText(path))!.Length==25,"启动时磁盘历史同样符合保留条数");
        SetAgentField(window,"_clipboard",clipboard);
        clipboard.ContentCopied+=(_,args)=>{copies++;PrivateCall(window,"OnClipboardContentCopied",clipboard,args);};
        clipboard.ContentPasted+=(_,_)=>pastes++;
        var options=AgentField<IslandOptions>(window,"_options");options.ClipboardNotifyEnabled=true;
        SettingsRegistry.Write(options,"ClipboardHistoryLimit",25);
        PrivateCall(window,"SetState",IslandState.Card,true,"clipboard");
        IntPtr foreground=NativeMethods.GetForegroundWindow();
        PrivateCall(window,"ExecuteClipboardItemCopy","history-20");PumpAnimations(250);
        Check(writes.SequenceEqual(new[]{"history-20"}) && clipboard.IsCopiedPending && copies==1 && pastes==0,
            "历史记录按钮只复制文本并报告已复制，不发送粘贴完成事件");
        Check(NativeMethods.GetForegroundWindow()==foreground && AgentField<IslandState>(window,"_state")==IslandState.Card,
            "复制历史记录不切换外部窗口焦点，也不关闭正在浏览的历史卡片");
        Check(clipboard.History.Count==25 && clipboard.History[0].Text=="history-20" && clipboard.History.Count(e=>e.Text=="history-20")==1,
            "复制旧记录更新到列表顶部且不会生成重复项");
        Check(window.ClipboardHistoryHint.Text.Contains("已复制"),"历史卡片显示复制成功反馈");
        SettingsRegistry.Write(options,"ClipboardHistoryLimit",12);PrivateCall(window,"OnClipboardOptionsChanged");
        Check(clipboard.History.Count==12 && window.ClipboardHistoryList.Items.Count==12,"减少保存条数立即裁剪服务与历史列表");
        Check(JsonSerializer.Deserialize<ClipboardEntry[]>(File.ReadAllText(path))!.Length==12 && !File.Exists(path+".bak"),
            "减少条数后磁盘和备份不再保留被移除的旧记录");
        clipboard.SetHistoryLimit(40);
        for(int i=0;i<20;i++) clipboard.CopyFromHistory("new-"+i);
        Check(clipboard.History.Count==32,"增加保留条数后可以继续保存超过 10 条历史");
        using(var loaded=new ClipboardService(Dispatcher.CurrentDispatcher,hotkeys,()=>sequence,()=>payload,path,_=>{}))
        {
            loaded.SetHistoryLimit(40);typeof(ClipboardService).GetMethod("LoadHistory",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(loaded,null);
            Check(loaded.History.Count==32 && loaded.History[0].Text=="new-19","重启加载保留设置允许的完整历史与顺序");
        }
        clipboard.SetHistoryLimit(0);clipboard.CopyFromHistory("copy-with-history-disabled");
        Check(clipboard.History.Count==0 && writes[^1]=="copy-with-history-disabled" && clipboard.IsCopiedPending,
            "0 条关闭历史保存，同时保持正常复制功能");
        clipboard.SetHistoryLimit(5000);Check(clipboard.HistoryLimit==100,"历史容量限制为 100 条");
        using var settingsLifetime=new ClipboardSettingsLifetime();var settings=settingsLifetime.Window;
        var preferences=new IslandOptions().Normalized();SettingsRegistry.Write(preferences,"ClipboardHistoryLimit",75);settings.LoadFrom(preferences);
        var box=(TextBox)settings.FindName("ClipboardHistoryLimitBox");int changed=0;settings.OptionsChanged+=(_,_)=>changed++;
        Check(box.Text=="75" && SettingsRegistry.All.Any(d=>d.Key=="ClipboardHistoryLimit" && ReferenceEquals(settings.FindName(d.ControlName!),box)),"设置窗口正确回填保存条数并绑定对应控件");
        box.Text="";Check(changed==0,"清空数字进行编辑时不会立即改变保存条数");
        box.Text="100";typeof(alpha_island.Settings.SettingsWindow).GetMethod("CommitClipboardHistoryLimit",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(settings,null);
        Check(changed==1 && (int)SettingsRegistry.Read((IslandOptions)typeof(alpha_island.Settings.SettingsWindow).GetField("_options",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(settings)!,"ClipboardHistoryLimit")! ==100,
            "确认输入后保存精确条数");
        box.Text="";typeof(alpha_island.Settings.SettingsWindow).GetMethod("CommitClipboardHistoryLimit",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(settings,null);
        Check(box.Text=="100","无效或空输入保留上次条数，不意外回退到 10 条");
        Check(SettingsRegistry.SearchWordsForPage(SettingsPageIds.Behavior).Contains("剪切板历史保存条数"),"设置搜索可以找到历史条数选项");
        PrivateCall(window,"SetState",IslandState.Bar,true,null);SetAgentField(window,"_clipboard",null);
    }
    private sealed class ClipboardSettingsLifetime:IDisposable
    {
        internal alpha_island.Settings.SettingsWindow Window {get;}=new(false);
        public void Dispose(){Window.ForceClose=true;Window.Close();}
    }
}