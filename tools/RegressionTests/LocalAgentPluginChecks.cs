using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AlphaIsland.Plugins.LocalAgents;

internal static partial class Program
{
    private static async Task AgentRpcFixture(string mode)
    {
        static Task Emit(object data)=>Console.Out.WriteLineAsync(JsonSerializer.Serialize(data));
        string session="fixture-thread";
        while(await Console.In.ReadLineAsync() is { } line)
        {
            using var document=JsonDocument.Parse(line);var msg=document.RootElement;
            string method=AgentProtocol.Text(msg,"method");var p=AgentProtocol.Child(msg,"params");var id=AgentProtocol.Child(msg,"id");
            if(mode=="antigravity")
            { await Emit(new{@event="result",result=new{status="SUCCESS",response="本机回复",conversation_id="fixture-conversation"}});continue; }
            if(method=="initialize")
            { await Emit(new{id,result=new{serverInfo=new{name="fixture"}}});continue; }
            if(method is "thread/start" or "thread/resume")
            {
                if(AgentProtocol.Text(p,"approvalPolicy")!="on-request"||AgentProtocol.Text(p,"sandbox")!="workspace-write") throw new Exception("Wrong approval policy");
                await Emit(new{id,result=new{thread=new{id=session}}});continue;
            }
            if(method=="turn/start")
            {
                string text=AgentProtocol.Text(AgentProtocol.Child(p,"input").EnumerateArray().First(),"text");
                await Emit(new{id,result=new{turn=new{id="fixture-turn",status="inProgress"}}});
                if(text=="block") { await Emit(new{method="item/agentMessage/delta",@params=new{threadId=session,delta="正在等待"}});continue; }
                await Emit(new{id="approval-test",method="item/commandExecution/requestApproval",@params=new{threadId=session,turnId="fixture-turn",command="fixture-read-only",reason="测试允许一次"}});continue;
            }
            if(id.ValueKind==JsonValueKind.String && id.GetString()=="approval-test")
            {
                string decision=AgentProtocol.Text(AgentProtocol.Child(msg,"result"),"decision");
                await Emit(new{method="item/agentMessage/delta",@params=new{threadId=session,delta=decision=="accept" ? "本机回复" : "操作已拒绝"}});
                await Emit(new{method="turn/completed",@params=new{threadId=session,turn=new{id="fixture-turn",status="completed"}}});continue;
            }
            if(method=="session/prompt")
            {
                session=AgentProtocol.Text(p,"sessionId");await Emit(new{id,result=new{messageId="receipt"}});
                await Emit(new{method="session.event",@params=new{sessionId=session,@event=new{type="assistant/message",data=new{message=new{content=new[]{new{type="text",text="本机回复"}}}}}}});
                await Emit(new{method="session.event",@params=new{sessionId=session,@event=new{type="turn/end",data=new{reason="completed"}}}});continue;
            }
        }
    }
    private static async Task LocalAgentProtocolChecks(string root)
    {
        string directory=Path.Combine(root,"agent task & folder");Directory.CreateDirectory(directory);
        string fixture=Environment.ProcessPath!;
        foreach(string provider in new[]{"codex","dsh","antigravity"})
        {
            var session=new AgentSession();session.Start=(agent,args,cwd)=>new(fixture,new[]{"--agent-rpc-fixture",provider},cwd);
            int asks=0;string answer="";
            await foreach(string text in session.RunAsync(new(provider,provider,"fixture","unused.js",directory),directory,"包含 ` $() & 的提问","test-model",root,Path.Combine(root,"config"),8765,
                (preview,ct)=>{asks++;return Task.FromResult(true);},CancellationToken.None)) answer+=text;
            Check(answer=="本机回复",provider+" 实际子进程协议将回复返回到岛内，路径和提示词不经过 shell");
            Check(asks==(provider=="codex" ? 1 : 0),provider+" 审批仅通过对应协议回调，未默认放行");
            if(provider=="codex")
            {
                answer="";
                await foreach(string text in session.RunAsync(new(provider,provider,"fixture"),directory,"第二回合","",root,Path.Combine(root,"config"),8765,(preview,ct)=>Task.FromResult(false),CancellationToken.None))answer+=text;
                Check(answer=="操作已拒绝"&&session.NativeSession=="fixture-thread","Codex 继续当前线程且拒绝结果原样返回");
            }
        }
        using(var cancel=new CancellationTokenSource(300))
        {
            int ownedId=0;var session=new AgentSession {Start=(agent,args,cwd)=>{var process=new RpcProcess(fixture,new[]{"--agent-rpc-fixture","codex"},cwd);ownedId=process.ProcessId;return process;}};
            bool cancelled=false;try { await foreach(var text in session.RunAsync(new("codex","Codex","fixture"),directory,"block","",root,Path.Combine(root,"config"),8765,(_,_)=>Task.FromResult(false),cancel.Token)) { } }
            catch(OperationCanceledException){cancelled=true;}
            bool exited;try{using var process=Process.GetProcessById(ownedId);exited=process.HasExited;}catch(ArgumentException){exited=true;}
            Check(cancelled&&ownedId>0&&exited,"停止操作会中断真实管道并关闭拥有的 Agent 进程");
        }
        using var foreign=JsonDocument.Parse("{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"foreign\",\"delta\":\"不属于本会话\"}}");
        Check(AgentProtocol.Decode("codex",foreign.RootElement,"own").Delta.Length==0,"其他原生会话的消息不会混入当前岛内会话");
        Check(!AgentProtocol.Redact("api_key=test-secret-123 Bearer test-token").Contains("test-secret")&&!AgentProtocol.Redact("Bearer test-token").Contains("test-token"),"本机 Agent 错误信息隐藏常见凭据格式");
        Check(File.Exists(Path.Combine(root,"config","plugins","ai.local-agents","sdk.patch.yml")),"Harness 的独立 SDK 补丁位于集中配置目录");
    }
    private static async Task LocalAgentHandshakeChecks()
    {
        var agents=AgentDiscovery.Find(alpha_island.Core.AppPaths.ConfigDir);
        foreach(var agent in agents.Where(a=>a.Id is "codex" or "dsh"))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            IReadOnlyList<string> args=agent.Id=="codex" ? new[]{"app-server"} : new[]{agent.Entry,"--profile","sdk"};
            await using var process=new RpcProcess(agent.Executable,args,Environment.CurrentDirectory,agent.Id=="dsh" ? agent.Home : null);
            object parameters=agent.Id=="codex" ? new{clientInfo=new{name="alpha_island_handshake",version="0.1.26"},capabilities=new{experimentalApi=true}} : (object)new{cwd=Environment.CurrentDirectory,provider="deepseek-official",model="deepseek-flash",maxTokens=128};
            var result=await process.CallAsync("initialize",parameters,deadline.Token);
            Check(result.ValueKind==JsonValueKind.Object,agent.Name+" 本机已安装程序完成实际协议初始化，未发送模型请求");
        }
        Check(agents.Any(a=>a.Id=="codex")&&agents.Any(a=>a.Id=="dsh"),"本机可自动发现 Codex 和 Harness，无需填写程序路径");
    }
}
