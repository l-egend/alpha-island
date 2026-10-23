using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlphaIsland.Speech;
using alpha_island.Core;
using alpha_island.Services;
using NAudio.Wave;

internal static partial class Program
{
    private static async Task SpeechProtocolChecks()
    {
        var pcm = Enumerable.Range(0, 16000).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 400 / 16000) * 10000)).ToArray();
        var audio = SpeechAudio.FromPcm(pcm); var wave = audio.Wave.ToArray();
        Check(wave.Length == 32044 && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)) == 16000
            && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)) == 1 && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)) == 16,
            "统一音频契约编码为有界的 16kHz 单声道 PCM16 WAV");
        var config = new SpeechRecognitionOptions("mimo", SpeechSettings.DefaultUrl, SpeechSettings.DefaultModel, TestApiKey, "zh");
        Check(!config.ToString().Contains(TestApiKey), "识别配置的字符串表示不暴露密钥");
        int calls = 0; var partials = new List<string>();
        using var http = new HttpClient(new AiTestHandler(async (request, token) =>
        {
            calls++;
            Check(request.RequestUri?.AbsoluteUri == "https://api.xiaomimimo.com/v1/chat/completions"
                && request.Headers.GetValues("api-key").Single() == TestApiKey, "MiMo 使用文档规定的端点和独立 api-key 鉴权");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var input = body.RootElement.GetProperty("messages");
            Check(input.GetArrayLength() == 1 && input[0].GetProperty("role").GetString() == "user"
                && input[0].GetProperty("content").GetArrayLength() == 1 && body.RootElement.GetProperty("asr_options").GetProperty("language").GetString() == "zh",
                "MiMo 请求只含本段音频及识别语种，不附带聊天人格或整段历史");
            string data = input[0].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!;
            Check(data.StartsWith("data:audio/wav;base64,") && Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]).SequenceEqual(wave),
                "MiMo 上传音频与标准 WAV 一致，没有重复或截断编码");
            return SseSpeech("你好", "，请调低音量。");
        }));
        var recognizer = new SpeechRecognizerRegistry(http).Create(config);
        var result = await recognizer.RecognizeAsync(audio, CancellationToken.None, new SpeechProgress(partials.Add));
        Check(result.Text == "你好，请调低音量。" && partials[0] == "你好" && partials[^1] == result.Text && calls == 1,
            "MiMo SSE 增量合并成完整文字，每段音频仅请求一次");

        using var jsonHttp = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"普通响应也可识别\"}}]}", Encoding.UTF8, "application/json") })));
        Check((await new ChatAudioSpeechRecognizer(config, jsonHttp).RecognizeAsync(audio, CancellationToken.None)).Text == "普通响应也可识别",
            "兼容服务返回非流式 JSON 时仍能读取识别结果");

        using var standardHttp = new HttpClient(new AiTestHandler(async (request, token) =>
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            Check(request.RequestUri?.AbsoluteUri == "http://localhost:8080/v1/audio/transcriptions"
                && request.Headers.Authorization == null && request.Content is MultipartFormDataContent
                && body.Contains("speech.wav") && body.Contains("native-small"), "本地转写适配器使用标准 multipart 文件接口且不要求云端密钥");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"本地识别成功\"}") };
        }));
        var registry = new SpeechRecognizerRegistry(standardHttp);
        var local = config with { Provider = "local-endpoint", BaseUrl = "http://localhost:8080/v1", Model = "native-small", ApiKey = "" };
        Check((await registry.Create(local).RecognizeAsync(audio, CancellationToken.None)).Text == "本地识别成功", "替换识别后端不改变音频调用契约");
        registry.Register("local-native", _ => new EchoSpeechRecognizer("内置引擎扩展"));
        Check((await registry.Create(config with { Provider = "local-native" }).RecognizeAsync(audio, CancellationToken.None)).Text == "内置引擎扩展",
            "本地原生模型可以直接注册适配器，无需修改语音和字幕调用方");

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            int attempts = 0;
            using var errors = new HttpClient(new AiTestHandler((_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(TestApiKey) }); }));
            string failure = "";
            try { await new ChatAudioSpeechRecognizer(config, errors).RecognizeAsync(audio, CancellationToken.None); }
            catch (SpeechRecognitionException error) { failure = error.Message; }
            Check(failure.Length > 0 && !failure.Contains(TestApiKey) && attempts == 1, $"HTTP {(int)status} 不泄露服务端正文且不自动重试计费请求");
        }
        foreach (string finish in new[] { "length", "content_filter", "truncated" })
        {
            using var incomplete = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(finish == "truncated" ? "data: {\"choices\":[{\"delta\":{\"content\":\"半句话\"}}]}\n\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"半句话\"},\"finish_reason\":\"" + finish + "\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") })));
            bool rejected = false;
            try { await new ChatAudioSpeechRecognizer(config, incomplete).RecognizeAsync(audio, CancellationToken.None); }
            catch (SpeechRecognitionException) { rejected = true; }
            Check(rejected, finish + " 的不完整识别不能变成已确认的桌面指令");
        }
        int cancelCalls = 0;
        using var cancellationHttp = new HttpClient(new AiTestHandler(async (_, token) =>
        { cancelCalls++; await Task.Delay(Timeout.Infinite, token); throw new Exception("unreachable"); }));
        using var cancel = new CancellationTokenSource(60); bool cancelled = false;
        try { await new ChatAudioSpeechRecognizer(config, cancellationHttp).RecognizeAsync(audio, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && cancelCalls == 1, "取消会中断正在等待的 HTTP 请求，没有自动重发");

        var segments = new SpeechSegmenter(); int uploads = 0; segments.Ready += _ => uploads++;
        for (int i = 0; i < 200; i++) segments.Push(new short[320]);
        Check(uploads == 0, "长时间无声不生成付费识别请求");
        for (int i = 0; i < 50; i++) segments.Push(pcm.AsSpan(i * 320, 320));
        for (int i = 0; i < 30; i++) segments.Push(new short[320]);
        Check(uploads == 1, "一秒语音后停顿产生一个有界音频段");
        segments.Flush(); Check(uploads == 1, "停止时不会重复上传已经识别的音频段");

        var converter = new PcmSpeechConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var encoded = new byte[48000 * 2 * 4];
        for (int i = 0; i < 48000; i++) for (int channel = 0; channel < 2; channel++)
            BitConverter.GetBytes((float)Math.Sin(i * 2 * Math.PI * 600 / 48000) * .25f).CopyTo(encoded, (i * 2 + channel) * 4);
        var converted = new List<short>(); converter.Convert(encoded, encoded.Length, memory => converted.AddRange(memory.ToArray()));
        int crossings = Enumerable.Range(1, converted.Count - 1).Count(i => converted[i - 1] < 0 && converted[i] >= 0);
        Check(converted.Count is > 15900 and < 16032 && crossings is > 590 and < 605, "实际 48kHz 双声道 WASAPI 数据经带滤波重采样后保持时长和语音频率");
        int firstSecond = converted.Count; converter.Convert(encoded, encoded.Length, memory => converted.AddRange(memory.ToArray()));
        Check(Math.Abs(converted.Count - firstSecond - 16000) <= 2, "持续重采样没有逐段增加延迟或音频时长漂移");

        using var source = new FakeSpeechAudioSource(); int finalSegments = 0;
        using var capture = new ProviderVoiceCapture(new EchoSpeechRecognizer("测试指令"), source);
        capture.SegmentRecognized += _ => finalSegments++;
        await capture.StartAsync(CancellationToken.None);
        source.Push(pcm);
        Check(await capture.FinishAsync(false, CancellationToken.None) == "测试指令" && source.Stopped && finalSegments == 1,
            "语音结束先停采集，再等待本段识别，完整确认后才交给助手");

        using var quietSource = new FakeSpeechAudioSource(); int idleSegments = 0;
        using var idleCapture = new ProviderVoiceCapture(new EchoSpeechRecognizer("暂停前最后一句"), quietSource, continuous: true);
        idleCapture.SegmentRecognized += _ => Interlocked.Increment(ref idleSegments);
        await idleCapture.StartAsync(CancellationToken.None); quietSource.Push(pcm);
        await Task.Delay(1000);
        Check(idleSegments == 1, "播放器暂停且 WASAPI 不再送静音帧时，最后一句字幕仍会自动识别");
    }
    private static async Task SpeechAudioSourceLiveChecks()
    {
        foreach (bool system in new[] { false, true })
        {
            using var source = new WasapiSpeechAudioSource(system); int samples = 0; string failure = "";
            source.Frame += frame => Interlocked.Add(ref samples, frame.Length); source.Failed += message => failure = message;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await source.StartAsync(timeout.Token); await Task.Delay(600, timeout.Token); source.Stop();
            Check(failure.Length == 0 && (system || samples > 0), system ? "实机默认输出的 WASAPI 采集可以启动并及时停止" : "实机默认麦克风可通过新共享采集路径获得 PCM 音频并及时停止");
            Console.WriteLine((system ? "System" : "Microphone") + " PCM sample count=" + samples + "; audio was neither saved nor uploaded.");
        }
    }
    private static HttpResponseMessage SseSpeech(params string[] parts) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("", parts.Select(text => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n"))
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
    };
    private sealed class SpeechProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class EchoSpeechRecognizer(string text) : ISpeechRecognizer
    {
        public Task<SpeechRecognitionResult> RecognizeAsync(SpeechAudio audio, CancellationToken token, IProgress<string>? partial = null)
        { token.ThrowIfCancellationRequested(); partial?.Report(text); return Task.FromResult(new SpeechRecognitionResult(text)); }
    }
    private sealed class FakeSpeechAudioSource : IPcmAudioSource
    {
        public event Action<ReadOnlyMemory<short>>? Frame;
        public event Action<string>? Failed;
        internal bool Started, Stopped, Disposed;
        public Task StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Started = true; return Task.CompletedTask; }
        internal void Push(short[] samples) => Frame?.Invoke(samples);
        internal void Error(string message) => Failed?.Invoke(message);
        public void Stop() => Stopped = true;
        public void Dispose() { Disposed = true; Stop(); }
    }
}
