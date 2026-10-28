using System.IO;
using AlphaIsland.PluginSdk;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace AlphaIsland.Plugins.LocalAgents;

internal static class AgentProtocol
{
    internal static string Text(JsonElement value, string key) => value.ValueKind==JsonValueKind.Object && value.TryGetProperty(key,out var result) && result.ValueKind==JsonValueKind.String ? result.GetString()! : "";
    internal static JsonElement Child(JsonElement value,string key) => value.ValueKind==JsonValueKind.Object && value.TryGetProperty(key,out var result) ? result : default;
    internal static string SafeError(JsonElement error) => Redact(Text(error,"message") is { Length: >0 } message ? message : "未返回详细原因");
    internal static string Redact(string text)
    {
        try
        {
            if (text.Length > 8000) text = text[..8000];
            text = Regex.Replace(text, @"\b(?:Bearer|Basic)\s+[^\s""',;]+", "[已隐藏]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"((?:[""']?)(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|authorization|cookie)(?:[""']?)\s*[:=]\s*)(?:\[已隐藏\]|""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}&\]]+)", "$1[已隐藏]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return text.Length > 1500 ? text[..1500] : text;
        }
        catch (RegexMatchTimeoutException) { return "错误内容无法安全脱敏，已省略。"; }
    }
    internal static (string Delta,bool Done,string Error) Decode(string provider,JsonElement message,string session)
    {
        var p=Child(message,"params"); string method=Text(message,"method");
        if(provider=="codex")
        {
            if(Text(p,"threadId") is {Length:>0} thread && thread!=session) return ("",false,"");
            if(method=="item/agentMessage/delta") return (Text(p,"delta"),false,"");
            if(method=="turn/completed")
            {
                var turn=Child(p,"turn"); string state=Text(turn,"status");
                return ("",true,state=="completed" ? "" : state=="interrupted" ? "任务已停止" : SafeError(Child(turn,"error")));
            }
        }
        if(provider=="dsh")
        {
            if(Text(p,"sessionId") is {Length:>0} id && id!=session) return ("",false,"");
            if(method=="session.event")
            {
                var e=Child(p,"event"); var data=Child(e,"data"); string type=Text(e,"type");
                if(type=="assistant/message")
                {
                    var content=Child(Child(data,"message"),"content");
                    if(content.ValueKind!=JsonValueKind.Array) content=Child(Child(data,"message"),"contentBlocks");
                    return (content.ValueKind==JsonValueKind.Array ? string.Concat(content.EnumerateArray().Where(b=>Text(b,"type")=="text").Select(b=>Text(b,"text"))) : "",false,"");
                }
                if(type=="turn/end")
                {
                    string reason=Text(data,"reason"); return ("",true, reason is "error" or "failed" ? "Harness 任务失败，请检查原应用中的模型与登录" : "");
                }
            }
        }
        if(provider=="antigravity")
        {
            string kind=Text(message,"event"); var step=Child(message,"step_update");
            if(kind=="step_update" && Text(step,"step_type")=="agent_response") return (Text(step,"text_delta"),false,"");
            if(kind=="result") { var result=Child(message,"result"); return ("",true,Text(result,"status")=="SUCCESS" ? "" : Redact(Text(result,"error"))); }
        }
        return ("",false,"");
    }
}

internal sealed class AgentSession
{
    internal string NativeSession {get;private set;}="";
    internal void RestoreSession(string id)
    { if(id.Length is >0 and <=256 && id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')) NativeSession=id; }
    internal Func<LocalAgent,IReadOnlyList<string>,string,RpcProcess> Start {get;set;}=(agent,args,cwd)=>new(agent.Executable,args,cwd,agent.Id=="dsh" ? agent.Home : null);
    internal async IAsyncEnumerable<string> RunAsync(LocalAgent agent,string directory,string prompt,string model,string pluginDirectory,string configDirectory,int bridgePort,
        Func<string,CancellationToken,Task<bool>> approve,[EnumeratorCancellation] CancellationToken token, IReadOnlyList<string>? files = null)
    {
        string[] attachments = files is { Count: > 0 } ? PluginFileInput.Validate(files) : [];
        prompt = AgentAttachments.WithPaths(prompt, attachments);
        object[] codexInput = agent.Id == "codex" ? AgentAttachments.CodexInput(prompt, attachments) : [];
        IReadOnlyList<string> args=agent.Id switch
        {
            "codex"=>new[]{"app-server"},
            "dsh"=>new[]{agent.Entry,"--profile","sdk","--patch",EnsureHarnessPatch(pluginDirectory,configDirectory,bridgePort)},
            _=>NativeSession.Length==0 ? new[]{"--input-format","stream-json","--output-format","stream-json"}
                : new[]{"--input-format","stream-json","--output-format","stream-json","--conversation",NativeSession}
        };
        await using var rpc=Start(agent,args,directory);
        if(agent.Id=="codex")
        {
            await rpc.CallAsync("initialize",new {clientInfo=new{name="alpha_island_local_agent",title="Alpha Island",version="0.1.26"},capabilities=new{experimentalApi=true}},token);
            await rpc.NotifyAsync("initialized",null,token);
            var thread=await rpc.CallAsync(NativeSession.Length==0 ? "thread/start" : "thread/resume",new {threadId=NativeSession.Length==0 ? null : NativeSession,cwd=directory,sandbox="workspace-write",approvalPolicy="on-request",approvalsReviewer="user"},token);
            NativeSession=AgentProtocol.Text(AgentProtocol.Child(thread,"thread"),"id");
            if(NativeSession.Length==0) throw new IOException("Codex 未返回会话标识");
            await rpc.CallAsync("turn/start",new {threadId=NativeSession,input=codexInput},token);
        }
        else if(agent.Id=="dsh")
        {
            await rpc.CallAsync("initialize",new {cwd=directory,provider="deepseek-official",model=string.IsNullOrWhiteSpace(model)?"deepseek-flash":model,maxTokens=4096},token);
            if(NativeSession.Length==0) NativeSession="alpha-island-"+Guid.NewGuid().ToString("N");
            await rpc.CallAsync("session/prompt",new {sessionId=NativeSession,contentBlocks=new[]{new{type="text",text=prompt}}},token);
        }
        else
        {
            await rpc.WriteAsync(new { @event="user", message=new{content=prompt} },token);
        }
        int characters=0;
        var fileChanges=new Dictionary<string,string>();
        await foreach(var message in rpc.Events.ReadAllAsync(token))
        {
            string method=AgentProtocol.Text(message,"method");
            if(agent.Id=="codex" && method=="item/started")
            {
                var p=AgentProtocol.Child(message,"params");var item=AgentProtocol.Child(p,"item");
                var changes=AgentProtocol.Child(item,"changes");
                if(AgentProtocol.Text(p,"threadId")==NativeSession && AgentProtocol.Text(item,"type")=="fileChange" && changes.ValueKind==JsonValueKind.Array)
                    fileChanges[AgentProtocol.Text(item,"id")]=string.Join("\n",changes.EnumerateArray().Select(c=>AgentProtocol.Text(c,"path")+"\n"+AgentProtocol.Text(c,"diff")));
            }
            if(agent.Id=="codex" && message.TryGetProperty("id",out var id) && method.Length!=0)
            {
                var p=AgentProtocol.Child(message,"params");
                bool own=AgentProtocol.Text(p,"threadId")==NativeSession;
                object result;
                if(method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                {
                    string? operation=AgentProtocol.Text(p,"command");
                    if(method=="item/fileChange/requestApproval") fileChanges.TryGetValue(AgentProtocol.Text(p,"itemId"),out operation);
                    // Only offer approval when the operation itself can be reviewed.
                    bool reviewable=!string.IsNullOrWhiteSpace(operation) && operation.Length<=12000;
                    string preview=(operation??"")+"\n"+AgentProtocol.Redact(AgentProtocol.Text(p,"reason"))+"\n目录："+directory;
                    bool allow=own && reviewable && await approve(preview,token); result=new{decision=allow?"accept":"decline"};
                }
                else if(method=="item/permissions/requestApproval") result=new{permissions=new{},scope="turn"};
                else if(method=="item/tool/requestUserInput") result=new{answers=new Dictionary<string,object>()};
                else if(method=="mcpServer/elicitation/request") result=new{action="decline",content=(object?)null};
                else result=new{success=false};
                await rpc.AnswerAsync(id,result,token); continue;
            }
            if(agent.Id=="antigravity")
            {
                string conversation=AgentProtocol.Text(message,"conversation_id");
                if(conversation.Length==0) conversation=AgentProtocol.Text(AgentProtocol.Child(message,AgentProtocol.Text(message,"event")),"conversation_id");
                if(conversation.Length!=0) RestoreSession(conversation);
            }
            var update=AgentProtocol.Decode(agent.Id,message,NativeSession);
            if(update.Delta.Length!=0) { characters+=update.Delta.Length; yield return update.Delta; }
            if(update.Done)
            {
                if(update.Error.Length!=0) throw new IOException(update.Error);
                if(characters==0 && agent.Id=="antigravity") { string answer=AgentProtocol.Text(AgentProtocol.Child(message,"result"),"response"); if(answer.Length!=0) {characters+=answer.Length;yield return answer;} }
                if(characters==0) throw new IOException("Agent 未返回可显示的回复，请检查原应用的模型和登录状态");
                yield break;
            }
        }
        throw new IOException("Agent 在任务完成前退出");
    }
    private static string EnsureHarnessPatch(string pluginDirectory,string configDirectory,int port)
    {
        string folder=Path.Combine(configDirectory,"plugins","ai.local-agents");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"sdk.patch.yml");
        string module=Path.Combine(pluginDirectory,"harness-approval.mjs").Replace('\\','/').Replace("'","''");
        File.WriteAllText(path,$"- insert:\n    - id: alpha-island-agent\n      name: '{module}'\n      config:\n        endpoint: 'http://127.0.0.1:{port}/'\n        timeoutSec: 120\n",new System.Text.UTF8Encoding(false));return path;
    }
}
