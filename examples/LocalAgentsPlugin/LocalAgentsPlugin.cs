using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using Microsoft.Win32;
namespace AlphaIsland.Plugins.LocalAgents;

public sealed class LocalAgentsPlugin : IIslandPlugin, IPluginSuspendGuard, IPluginDiagnostics
{
    public string DiagnosticText => (_agents.Any(agent => agent.Id == "codex") ? "CODEX_CLI_FOUND" : "CODEX_CLI_NOT_FOUND")
        + "; probes=PATH,npm,desktop,MSIX,VSCode,manual; providers=" + string.Join(",", _agents.Select(agent => agent.Id))
        + "; status=" + (_view?.DiagnosticStatus ?? "未打开对话") + "; authentication=not-probed; " + (_view?.LastFailure ?? "");
    public bool CanSuspend => _view?.CanSuspendForGame ?? true;
    private LocalAgentsView? _view;
    private IPluginContext _context=null!;
    private IReadOnlyList<LocalAgent> _agents=Array.Empty<LocalAgent>();
    public void Initialize(IPluginContext context)
    {
        _context=context; _agents=AgentDiscovery.Find(context.ConfigDirectory);
        context.RegisterPage(new("tasks","本机 Agent",()=>_view=new(context,_agents),"\uE99A",320,101)
            { Available=()=>true,Summary=()=>_view?.Summary??"本机 Agent" });
        context.RegisterAction(new("local-agents-open","本机 Agent",()=>context.OpenPage("tasks"),()=>true));
        context.RegisterSettingsView(()=>Settings());
    }
    private FrameworkElement Settings()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        panel.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/Win11Settings.xaml") });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
        void Update()
        {
            status.Text = (_agents.Count == 0 ? "未找到可调用的 Agent CLI" : "已发现：" + string.Join("、", _agents.Select(agent => agent.Name)))
                + "\n复用原账号；Codex 支持常见安装位置，也可手动选择 codex.exe。";
            _context.Log("Agent discovery: codex=" + _agents.Any(agent => agent.Id == "codex") + "; harness=" + _agents.Any(agent => agent.Id == "dsh")
                + "; antigravity=" + _agents.Any(agent => agent.Id == "antigravity"));
        }
        void Refresh()
        {
            _agents = AgentDiscovery.Find(_context.ConfigDirectory); _view?.SetAgents(_agents); Update(); _context.Refresh();
        }
        bool CanEdit()
        { if (_view?.Busy != true) return true; status.Text = "请先停止当前任务。"; return false; }
        Update(); panel.Children.Add(status);
        var actions = new WrapPanel();
        Button Action(string label, Action action)
        {
            var button = new Button { Content = label, Style = (Style)panel.FindResource("W11Button"), Margin = new Thickness(0, 4, 8, 0) };
            button.Click += (_, _) => action(); actions.Children.Add(button); return button;
        }
        Action("重新检测", () => { if (CanEdit()) Refresh(); });
        Action("打开本机 Agent", () => _context.OpenPage("tasks"));
        Action("选择 Codex 程序", () =>
        {
            if (!CanEdit()) return;
            var picker = new OpenFileDialog { Title = "选择 Codex CLI（codex.exe）", Filter = "Codex CLI|codex.exe" };
            if (picker.ShowDialog() != true) return;
            try { AgentDiscovery.SetCodexExecutable(_context.ConfigDirectory, picker.FileName); Refresh(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
        });
        Action("重置路径", () =>
        {
            if (!CanEdit()) return;
            try { AgentDiscovery.SetCodexExecutable(_context.ConfigDirectory, null); Refresh(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
        });
        panel.Children.Add(actions); return panel;
    }
    public void Dispose() { _view?.Dispose(); _view=null; }
}

internal sealed partial class LocalAgentsView : UserControl,IDisposable
{
    private readonly IPluginContext _context;
    private readonly ComboBox _provider=new(){Width=135,FontSize=11};
    private readonly TextBox _input=new(){Name="LocalAgentInput",FontSize=12,MaxLength=12000,Padding=new Thickness(8,5,8,5)};
    private readonly TextBox _output=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
        Background=Brushes.Transparent,Foreground=Brushes.White,BorderThickness=new Thickness(0),FontSize=11,Padding=new Thickness(5)};
    private readonly TextBlock _status=new(){FontSize=10,Foreground=new SolidColorBrush(Color.FromRgb(155,205,220)),TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _directory=new(){FontSize=10,Foreground=Brushes.LightGray,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBox _approvalText=new(){FontSize=11,Foreground=Brushes.White,Background=Brushes.Transparent,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=110};
    private readonly StackPanel _approval=new(){Visibility=Visibility.Collapsed};
    private Button _send=null!,_stop=null!,_choose=null!,_new=null!;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _approvalAnswer;
    private AgentSession _session=new();
    private bool _disposed,_setting;
    private string _cwd="";
    private readonly string _historyPath;
    internal bool Busy=>_operation!=null;
    internal string DiagnosticStatus => _status.Text;
    internal string LastFailure { get; private set; } = "";
    internal bool CanSuspendForGame => !Busy && _input.Text.Length == 0 && _output.Text.Length == 0 && _attachmentPaths.Count == 0;
    internal string Summary=>_operation==null ? "本机 Agent" : _approvalAnswer!=null ? "等待确认" : "正在处理任务";
    internal LocalAgentsView(IPluginContext context,IReadOnlyList<LocalAgent> agents)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/alpha-island;component/Themes/Controls.xaml")});
        _provider.Style=(Style)Resources[typeof(ComboBox)];
        _provider.Foreground=Brushes.White;
        _provider.Background=new SolidColorBrush(Color.FromRgb(35,39,49));
        _provider.Resources["ControlPopupBrush"]=new SolidColorBrush(Color.FromRgb(24,27,34));
        _provider.Resources["ControlTextBrush"]=Brushes.White;
        _input.Style=(Style)Resources["ThemedTextBox"];
        _input.Foreground=_input.CaretBrush=Brushes.White;
        _input.Background=new SolidColorBrush(Color.FromArgb(24,255,255,255));
        _approvalText.Style=(Style)Resources["ThemedTextBox"];
        _context=context; _historyPath=Path.Combine(context.ConfigDirectory,"plugins","ai.local-agents","last-conversation.json");
        var root=new Grid {Margin=new Thickness(6)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto}) root.RowDefinitions.Add(new(){Height=height});
        var header=new Grid();header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=new StackPanel();title.Children.Add(new TextBlock {Text="本机 Agent",Foreground=Brushes.White,FontSize=13,FontWeight=FontWeights.SemiBold});title.Children.Add(_status);header.Children.Add(title);
        var actions=new StackPanel {Orientation=Orientation.Horizontal};actions.Children.Add(_provider);
        _new=Button("新建",()=>{ResetConversation();ClearAttachments();});actions.Children.Add(_new);Grid.SetColumn(actions,1);header.Children.Add(actions);root.Children.Add(header);
        var folder=new Grid {Margin=new Thickness(0,6,0,4)};folder.ColumnDefinitions.Add(new());folder.ColumnDefinitions.Add(new(){Width=GridLength.Auto});folder.Children.Add(_directory);
        _choose=Button("选择目录",ChooseDirectory);Grid.SetColumn(_choose,1);folder.Children.Add(_choose);Grid.SetRow(folder,1);root.Children.Add(folder);
        Grid.SetRow(_output,2);root.Children.Add(_output);
        InitializeFileBar(root);
        _approval.Children.Add(_approvalText);var answers=new StackPanel {Orientation=Orientation.Horizontal};answers.Children.Add(Button("允许一次",()=>_approvalAnswer?.TrySetResult(true)));answers.Children.Add(Button("拒绝",()=>_approvalAnswer?.TrySetResult(false)));_approval.Children.Add(answers);Grid.SetRow(_approval,4);root.Children.Add(_approval);
        var input=new Grid {Margin=new Thickness(0,5,0,0)};input.ColumnDefinitions.Add(new());input.ColumnDefinitions.Add(new(){Width=GridLength.Auto});input.ColumnDefinitions.Add(new(){Width=GridLength.Auto});input.Children.Add(_input);
        _send=Button("发送",()=>_ = SendAsync());Grid.SetColumn(_send,1);input.Children.Add(_send);
        _stop=Button("停止",()=>_operation?.Cancel());Grid.SetColumn(_stop,2);input.Children.Add(_stop);Grid.SetRow(input,5);root.Children.Add(input);Content=root;
        _setting=true;SetAgents(agents);_cwd=context.ReadConfiguration("plugin/ai.local-agents/workspace","");
        if(!Directory.Exists(_cwd)) _cwd="";_directory.Text=_cwd.Length==0 ? "选择任务目录后发送" : _cwd;_directory.ToolTip=_cwd;
        _provider.SelectedItem=agents.FirstOrDefault(a=>a.Id==context.ReadConfiguration("plugin/ai.local-agents/provider","codex"))??agents.FirstOrDefault();_setting=false;
        _provider.SelectionChanged+=(_,_)=>{if(!_setting && _operation==null) {ResetConversation();SaveChoice();}};
        _input.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){e.Handled=true;_ = SendAsync();}};
        _input.TextChanged+=(_,_)=>UpdateControls();LoadHistory();if(agents.Count==0)_status.Text="未检测到可调用的 Agent，请到插件设置重新检测或选择程序。";UpdateControls();
    }
    private Button Button(string text,Action action)
    {
        var button=new Button {Style=(Style)Resources["ThemedButton"],Content=text,Margin=new Thickness(5,0,0,0),Padding=new Thickness(7,3,7,3),FontSize=10,
            Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(48,100,130,155)),BorderBrush=new SolidColorBrush(Color.FromArgb(64,160,190,210))};button.Click+=(_,_)=>action();return button;
    }
    internal void SetAgents(IReadOnlyList<LocalAgent> agents)
    {
        string? current=(_provider.SelectedItem as LocalAgent)?.Id;
        _setting=true;_provider.ItemsSource=agents;_provider.SelectedItem=agents.FirstOrDefault(a=>a.Id==current)??agents.FirstOrDefault();_setting=false;
        if (_send != null) UpdateControls();
    }
    private void ChooseDirectory()
    {
        var dialog=new OpenFolderDialog {Title="选择本机 Agent 的任务目录",InitialDirectory=_cwd.Length!=0 ? _cwd : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};
        if(dialog.ShowDialog(Window.GetWindow(this))!=true) return;
        _cwd=dialog.FolderName;_directory.Text=_cwd;_directory.ToolTip=_cwd;ResetConversation();SaveChoice();UpdateControls();
    }
    private void SaveChoice()
    { _context.WriteConfiguration("plugin/ai.local-agents/workspace",_cwd);_context.WriteConfiguration("plugin/ai.local-agents/provider",(_provider.SelectedItem as LocalAgent)?.Id??""); }
    private void ResetConversation()
    { if(_operation!=null) return;_session=new();_output.Text="";_status.Text="新会话 · 使用原应用登录";SaveHistory(); }
    private void UpdateControls()
    {
        bool busy=_operation!=null;_send.IsEnabled=!_disposed&&!busy&&_provider.SelectedItem is LocalAgent&&Directory.Exists(_cwd)&&(!string.IsNullOrWhiteSpace(_input.Text)||_attachmentPaths.Count>0);
        _stop.IsEnabled=busy;_choose.IsEnabled=_new.IsEnabled=_provider.IsEnabled=!busy;_input.IsEnabled=!busy;if(_clearFiles!=null)_clearFiles.IsEnabled=!busy;
    }
    internal async Task SendAsync()
    {
        if(_operation!=null||_disposed||_provider.SelectedItem is not LocalAgent agent||!Directory.Exists(_cwd)||(string.IsNullOrWhiteSpace(_input.Text)&&_attachmentPaths.Count==0)) return;
        string[] attachments;
        try { attachments = _attachmentPaths.Count == 0 ? [] : PluginFileInput.Validate(_attachmentPaths); }
        catch (Exception error) when (error is IOException or ArgumentException) { _status.Text = error.Message; return; }
        string prompt=string.IsNullOrWhiteSpace(_input.Text) ? "请查看附件，概述内容并询问我希望如何处理。" : _input.Text.Trim();_input.Text="";_output.AppendText("\n你："+prompt+"\n"+agent.Name+"：\n");
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);cancel.CancelAfter(TimeSpan.FromMinutes(10));_operation=cancel;UpdateControls();
        bool completed = false; LastFailure = "";
        _status.Text="正在连接 "+agent.Name;_context.ReportActivity("thinking",agent.Name);
        try
        {
            await foreach(string chunk in _session.RunAsync(agent,_cwd,prompt,_context.ReadConfiguration("plugin/ai.local-agents/harness-model","deepseek-flash"),_context.PluginDirectory,_context.ConfigDirectory,
                _context.ReadConfiguration("AgentBridgePort",8765),AskApprovalAsync,cancel.Token,attachments))
            { if(_disposed) return;_status.Text="正在回复 · "+agent.Name;_context.ReportActivity("streaming",agent.Name);_output.AppendText(chunk);TrimOutput();_output.ScrollToEnd(); }
            completed = true;
            _status.Text="已完成 · "+agent.Name;
        }
        catch(OperationCanceledException){if(!_disposed){_output.AppendText("\n（已停止）");_status.Text="任务已停止";}}
        catch(Exception ex){if(!_disposed){LastFailure=AgentProtocol.Redact(ex.GetType().Name+": "+ex.Message);_status.Text="调用失败 · 详细原因见下方";_context.Log("Local Agent call failed: "+AgentProtocol.Redact(ex.Message));_output.AppendText("\n"+AgentProtocol.Redact(ex.Message));}}
        finally
        {
            _operation=null;_approvalAnswer?.TrySetResult(false);_approvalAnswer=null;_approval.Visibility=Visibility.Collapsed;
            if(!_disposed){if(completed)ClearAttachments();_context.ReportActivity("idle","");SaveHistory();UpdateControls();}
        }
    }
    private async Task<bool> AskApprovalAsync(string preview,CancellationToken token)
    {
        _approvalAnswer=new(TaskCreationOptions.RunContinuationsAsynchronously);_approvalText.Text="此操作需要确认：\n"+preview;_approval.Visibility=Visibility.Visible;
        _status.Text="等待确认 · 允许仅本次";_context.ReportActivity("waiting","本机 Agent");_context.NotifyPage("tasks","本机 Agent 请求确认",preview,12);
        try { return await _approvalAnswer.Task.WaitAsync(TimeSpan.FromMinutes(2),token); }
        catch(TimeoutException){return false;}
        finally { _approvalAnswer=null;_approval.Visibility=Visibility.Collapsed; }
    }
    private void LoadHistory()
    {
        try { if(new FileInfo(_historyPath).Length>2_000_000)return;using var data=JsonDocument.Parse(File.ReadAllText(_historyPath));var root=data.RootElement;if(AgentProtocol.Text(root,"directory")==_cwd&&AgentProtocol.Text(root,"provider")==(_provider.SelectedItem as LocalAgent)?.Id) {_output.Text=AgentProtocol.Text(root,"text");TrimOutput();_session.RestoreSession(AgentProtocol.Text(root,"session"));} }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);string temporary=_historyPath+".tmp";
            TrimOutput();File.WriteAllText(temporary,JsonSerializer.Serialize(new{provider=(_provider.SelectedItem as LocalAgent)?.Id,directory=_cwd,session=_session.NativeSession,text=_output.Text}),new UTF8Encoding(false));
            File.Move(temporary,_historyPath,true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_status.Text="任务记录保存失败，请检查配置目录";}
    }
    private void TrimOutput()
    { const int limit=200000;if(_output.Text.Length>limit)_output.Text="（较早显示内容已省略，原生会话上下文保留）\n"+_output.Text[^limit..]; }
    public void Dispose(){if(_disposed)return;_disposed=true;_operation?.Cancel();_approvalAnswer?.TrySetResult(false);_context.ReportActivity("idle","");}
}
