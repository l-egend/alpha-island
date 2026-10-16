using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    private static void CodexStatusRecoveryChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        int FreePort() { var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port; }
        int port=FreePort();string endpoint=$"http://127.0.0.1:{port}/", runtime=Path.Combine(testRoot,"codex-runtime");
        string config=Path.Combine(testRoot,"codex-config.json"),offlineConfig=Path.Combine(testRoot,"offline-config.json");
        string transcript=Path.Combine(testRoot,"codex-history.jsonl");
        const string frontend="recovery-conversation";
        string session=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frontend))).ToLowerInvariant()[..24];
        string owner="external:codex/"+session;
        File.WriteAllText(transcript,JsonSerializer.Serialize(new{type="session_meta",payload=new{id=frontend,session_id=frontend,source="vscode"}})+"\n");
        File.WriteAllText(config,JsonSerializer.Serialize(new{endpoint,runtimeDir=runtime,allProjects=true,approvalMode="native"}));
        File.WriteAllText(offlineConfig,JsonSerializer.Serialize(new{endpoint=$"http://127.0.0.1:{FreePort()}/",runtimeDir=runtime,allProjects=true,approvalMode="native"}));
        string directory=Path.Combine(runtime,"status-events");
        string node=Path.GetFullPath("bin/InstallerTools/node-v24.21.0/node.exe"),adapter=Path.GetFullPath("examples/CodexHooks/hook.mjs");
        async Task<bool> Hook(string eventName,string turn)
        {
            var start=new ProcessStartInfo(node){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(adapter);start.ArgumentList.Add("--config");start.ArgumentList.Add(offlineConfig);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new{session_id=frontend,turn_id=turn,transcript_path=transcript,cwd=testRoot,hook_event_name=eventName,tool_name="Bash",prompt="private-recovery-sentinel"}));process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return process.ExitCode==0 && (await output).Trim()=="{}" && string.IsNullOrWhiteSpace(await errors);
        }
        void Until(Func<bool> done,int timeout=2200) {var watch=Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<timeout)PumpAnimations(10);}
        using var destination=new HwndSource(new HwndSourceParameters("Minimized Codex recovery fixture")
            {Width=640,Height=420,PositionX=-31000,PositionY=-31000,WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x08000080});
        var target=new AgentAppWindow(destination.Handle,"Codex","Recovery fixture",true);
        var fixture=new AgentWindowFixture{Windows=[target],Foreground=destination.Handle};
        var navigation=AgentField<AgentAppNavigationService>(window,"_agentNavigation");
        var options=AgentField<IslandOptions>(window,"_options");var saved=new Dictionary<string,JsonElement>(options.Extras);
        var layers=AgentField<IslandLayerHost>(window,"_layers");
        string? oldHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        var journal=new CodexStatusJournal(endpoint,config,_=>Environment.ProcessId);
        using var bridge=new AgentBridgeService(port,_=>false,false,journal);
        using var http=new HttpClient{BaseAddress=new Uri(endpoint)};
        bool Active()=>AgentStatus.Snapshot.Any(a=>a.Owner==owner);
        long Activity()=>AgentStatus.Snapshot.Single(a=>a.Owner==owner).ActivityId;
        async Task<bool> Send(string payload) {using var response=await http.PostAsync("status",new StringContent(payload,Encoding.UTF8,"application/json"));return response.IsSuccessStatusCode;}
        string Queue(string state,string turn,long sent,bool begins=false)
        {
            string id=Guid.NewGuid().ToString("D");
            string payload=JsonSerializer.Serialize(new{source="codex",session,state,detail="Codex fixture",ttlSec=300,statusEventId=id,reportedAtUnixMs=sent,
                reporterPid=Environment.ProcessId,nativeThreadId=frontend,nativeTurnId=turn,beginsActivity=begins});
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,$"{sent:D13}-{id}.json"),"{\"version\":1,\"payload\":"+payload+"}");return payload;
        }
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(testRoot,"empty-codex-home"));
            SettingsRegistry.Write(options,"AgentStatusEnabled",true);SettingsRegistry.Write(options,"MediaWindowHandoffEnabled",true);
            SetAgentField(window,"_agentNavigation",new AgentAppNavigationService(fixture));PrivateCall(window,"StartAgentStatus");
            NativeMethods.ShowWindowAsync(destination.Handle,6);Until(()=>NativeMethods.IsIconic(destination.Handle),300);
            Check(AwaitPlugin(Hook("UserPromptSubmit","recovery-turn-1")),"真实 Node 适配器在首次提交报送失败时正常返回");
            string pending=File.ReadAllText(Directory.GetFiles(directory,"*.json").Single());
            Check(!pending.Contains("private-recovery-sentinel"),"状态补报只保存必要元数据，不记录用户任务内容");
            using var pendingDocument=JsonDocument.Parse(pending);
            string payload=pendingDocument.RootElement.GetProperty("payload").GetRawText();
            var watch=Stopwatch.StartNew();bridge.Start();
            string readiness=JsonSerializer.Serialize(new{source="codex",session,state="idle",connectOnly=true,statusEventId=Guid.NewGuid().ToString("D"),reportedAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});
            Check(AwaitPlugin(Send(readiness)) && !Active(),"提交之后迟到的会话初始化只表示接入，不冒充任务开始或结束");
            Until(Active);
            Check(Active() && watch.ElapsedMilliseconds<1800,"丢失的首次提交在现有状态周期中自动补报，不需要重新打开 Codex");
            root.UpdateLayout();
            Check(NativeMethods.IsIconic(destination.Handle) && layers.IsActive("agentstatus") && window.AgentNotifyLayer.Visibility==Visibility.Visible,
                "提交后已最小化的 Codex 仍正确显示工作岛，即使前台切换信息尚未更新");
            long first=Activity();Check(AwaitPlugin(Send(payload)) && Activity()==first,"HTTP 与本地补报收到同一个提交时只开启一次任务，不重复弹出");
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();string next=Queue("thinking","recovery-turn-2",now,true);
            string earlyTool=Queue("working","recovery-turn-2",now+1);
            Check(AwaitPlugin(Send(earlyTool)) && Activity()!=first && AgentStatus.Snapshot.Single(a=>a.Owner==owner).State==AgentRunState.Working,
                "工具执行事件先到达时先补齐漏掉的新任务开始，保持执行图标与真实任务一致");
            long beforeDuplicate=Activity();
            Check(AwaitPlugin(Send(next)) && Activity()==beforeDuplicate,"补报过的新任务不会因 HTTP 重复事件再次重置计时或弹出");
            long second=Activity();Queue("thinking","recovery-turn-1",now-1000,true);
            Until(()=>Directory.GetFiles(directory,"*.json").Length==0);
            Check(Activity()==second,"旧任务的补报晚于新任务到达时，不覆盖当前任务或重新弹出");
            Check(AwaitPlugin(Hook("Stop","recovery-turn-2")),"真实结束事件在 HTTP 失败时保留到补报队列");
            Until(()=>!Active());root.UpdateLayout();
            Check(!Active() && !layers.IsActive("agentstatus"),"完成事件漏报后，最小化助手的思考状态也自动收回");
            string late=Queue("working","recovery-turn-2",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Check(AwaitPlugin(Send(late)) && !Active(),"完成之后迟到的同轮工具回包不重新创建助手岛");
            File.WriteAllText(Path.Combine(testRoot,"integrations.json"),"{\"providers\":{\"codex\":{\"enabled\":false}}}");
            Queue("thinking","disabled-turn",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),true);
            journal.Drain((ExternalAgentRegistry)typeof(AgentBridgeService).GetField("_externalAgents",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!,DateTime.UtcNow);
            Check(!Active(),"用户停用 Codex 接入后不通过状态补报重新开启");
        }
        finally
        {
            bridge.Stop();PrivateCall(window,"StopAgentStatus");SetAgentField(window,"_agentNavigation",navigation);options.Extras=saved;
            Environment.SetEnvironmentVariable("CODEX_HOME",oldHome);NativeMethods.ShowWindowAsync(destination.Handle,NativeMethods.SW_RESTORE);
        }
    }
}