using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private sealed class AiTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token);
    }

    private sealed class AiBlockingStream(string prefix) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_offset < _bytes.Length)
            {
                int length = Math.Min(buffer.Length, _bytes.Length - _offset);
                _bytes.AsMemory(_offset, length).CopyTo(buffer); _offset += length; return length;
            }
            await Task.Delay(Timeout.Infinite, token); return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task AiIntegrationChecks()
    {
        var config = new ChatConfig("https://example.invalid/v1/", TestApiKey, "test-model", "测试提示", .5, 512);
        Check(config.IsUsable && config.Endpoint == "https://example.invalid/v1/chat/completions", "兼容 Base URL 正确生成对话接口");
        Check((config with { BaseUrl = config.Endpoint }).Endpoint == config.Endpoint, "直接填写完整接口不会重复追加路径");
        Check(!(config with { BaseUrl = "bad-address" }).IsUsable
            && !(config with { BaseUrl = "file:///C:/test" }).IsUsable
            && !(config with { BaseUrl = "https://user:password@example.com/v1" }).IsUsable, "拒绝无效协议和夹带凭据的接口地址");
        Check(!config.ToString().Contains(TestApiKey), "配置对象的字符串表示隐藏密钥");
        static HttpResponseMessage Response(HttpStatusCode status, string text) => new(status)
            { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
        static async Task<string> Collect(ChatService service, ChatConfig cfg, CancellationToken token = default)
        {
            var output = new StringBuilder();
            await foreach (string chunk in service.StreamAsync(new[] { new ChatMessage("user", "连通性测试") }, cfg, token)) output.Append(chunk);
            return output.ToString();
        }
        foreach (int status in new[] { 401, 403, 404, 422, 429, 500 })
        {
            using var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(Response((HttpStatusCode)status,
                JsonSerializer.Serialize(new { error = new { message = "failed key=" + TestApiKey } })))));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            bool failed = false;
            try { await Collect(service, config); } catch (ChatException ex) { failed = ex.Message.Contains(status.ToString()) && !ex.Message.Contains(TestApiKey); }
            Check(failed && !service.LastError.Contains(TestApiKey), "HTTP " + status + " 可解释且错误中密钥被打码");
        }
        using (var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK,
            ": heartbeat\n\ndata: {bad\n\ndata: {\"choices\":[]}\n\ndata: {\"choices\":[{\"delta\":null}]}\n\ndata: {\"choices\":[{\"message\":{\"content\":\"兼容回复\"}}]}\n\ndata: [DONE]\n\n")))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
            Check(await Collect(service, config) == "兼容回复", "心跳、坏 JSON 与空增量不影响兼容流式回复");
        using (var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK,
            "data: " + JsonSerializer.Serialize(new { error = new { message = TestApiKey } }) + "\n\n")))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        {
            bool failed = false; try { await Collect(service, config); } catch (ChatException ex) { failed = !ex.Message.Contains(TestApiKey) && ex.Message.Contains("已隐藏"); }
            Check(failed, "流式错误也会隐藏被服务端回显的密钥");
        }
        using (var http = new HttpClient(new AiTestHandler(async (_, token) =>
            { await Task.Delay(Timeout.Infinite, token); return Response(HttpStatusCode.OK, ""); })))
        using (var service = new ChatService(http, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80)))
        {
            bool timedOut = false; try { await Collect(service, config); } catch (ChatException ex) { timedOut = ex.Message.Contains("超时"); }
            Check(timedOut, "连接超过等待上限后报超时而非无限等待");
        }
        using (var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new AiBlockingStream("data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n")) }))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(80)))
        {
            string partial = ""; bool timedOut = false;
            try { await foreach (string part in service.StreamAsync(new[] { new ChatMessage("user", "测试") }, config)) partial += part; }
            catch (ChatException ex) { timedOut = ex.Message.Contains("超时"); }
            Check(timedOut && partial == "部分", "流中断超时前已接收内容仍能交给 UI");
        }
        using (var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new AiBlockingStream("")) }))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
        using (var cancel = new CancellationTokenSource(80))
        {
            bool stopped = false; try { await Collect(service, config, cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && service.LastError == "", "用户停止生成保留取消语义，不误报网络错误");
        }
        using (var http = new HttpClient(new AiTestHandler((_, _) => throw new Exception("invalid request must not reach HTTP"))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
        {
            bool rejected = false; try { await Collect(service, config with { Temperature = double.NaN }); } catch (ChatException) { rejected = true; }
            Check(rejected, "无效请求参数在发送前被拒绝");
        }

        // 真正的本机 HTTP 服务验证鉴权、请求 JSON、网络分段与 UTF8 流式解析。
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        string? requestBody = null; bool authorization = false; string? route = null;
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            authorization = context.Request.Headers["Authorization"] == "Bearer " + TestApiKey;
            route = context.Request.Url?.AbsolutePath;
            using var reader = new StreamReader(context.Request.InputStream); requestBody = await reader.ReadToEndAsync();
            context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
            byte[] content = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"reasoning_content\":\"思考\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"已收到\"}}]}\n\ndata: [DONE]\n\n");
            for (int offset = 0; offset < content.Length; offset += 7)
            {
                await context.Response.OutputStream.WriteAsync(content.AsMemory(offset, Math.Min(7, content.Length - offset)));
                await context.Response.OutputStream.FlushAsync();
            }
            context.Response.Close();
        });
        using (var service = new ChatService())
            Check(await Collect(service, config with { BaseUrl = $"http://127.0.0.1:{port}/v1" }) == "已收到", "真实本机 HTTP 流式对话可完成，跨字节中文分段正确解析");
        await server.WaitAsync(TimeSpan.FromSeconds(3)); listener.Stop();
        using var request = JsonDocument.Parse(requestBody!);
        Check(authorization && route == "/v1/chat/completions", "真实请求发向正确端点并使用 Bearer 鉴权");
        Check(request.RootElement.GetProperty("model").GetString() == "test-model" && request.RootElement.GetProperty("stream").GetBoolean()
            && request.RootElement.GetProperty("temperature").GetDouble() == .5 && request.RootElement.GetProperty("max_tokens").GetInt32() == 512, "模型、流式及生成参数正确发出");
        Check(request.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system"
            && request.RootElement.GetProperty("messages")[1].GetProperty("role").GetString() == "user", "系统提示与用户消息正确组装");
        Check(!requestBody!.Contains(TestApiKey) && !IslandLog.DumpText().Contains(TestApiKey), "API Key 只发在鉴权头，不进入请求 JSON 或日志");
        bool omitted = false;
        using (var http = new HttpClient(new AiTestHandler((req, _) =>
            { omitted = req.Headers.Authorization == null; return Task.FromResult(Response(HttpStatusCode.OK, "data: [DONE]\n\n")); })))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            await Collect(service, config with { ApiKey = "" });
        Check(omitted, "无密钥的本地模型请求不发送鉴权头");
    }
}
