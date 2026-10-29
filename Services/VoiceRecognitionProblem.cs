using L = AlphaIsland.PluginSdk.Localization.UiText;
namespace alpha_island.Services;

internal sealed record VoiceRecognitionProblem(string Title, string Summary, string Detail, string SettingsUri)
{
    internal const string SpeechSettings = "ms-settings:speech", MicrophoneSettings = "ms-settings:privacy-microphone";
    internal static VoiceRecognitionProblem FromMessage(string detail)
    {
        if (detail.Contains("语音识别", StringComparison.Ordinal) || detail.Contains("MiMo", StringComparison.Ordinal))
            return new(L.T("语音识别暂不可用"), L.T("点击检查语音模型与密钥"), detail, "island:speech");
        if (detail.Contains("没有听清", StringComparison.Ordinal))
            return new(L.T("没有听清，请重试"), L.T("未发送，可重新唤醒后再说"), detail, MicrophoneSettings);
        if (detail.Contains("麦克风", StringComparison.Ordinal) || detail.Contains("录音设备", StringComparison.Ordinal))
            return new(L.T("麦克风暂不可用"), L.T("点击检查麦克风与权限"), detail, MicrophoneSettings);
        if (detail.Contains("联机语音", StringComparison.Ordinal))
            return new(L.T("系统语音服务未就绪"), L.T("点击检查 Windows 语音设置"), detail, SpeechSettings);
        if (detail.Contains("API", StringComparison.Ordinal) || detail.Contains("接口", StringComparison.Ordinal))
            return new(L.T("助手暂未就绪"), L.T("点击检查 API 对话设置"), detail, "");
        if (detail.Contains("没有识别到", StringComparison.Ordinal))
            return new(L.T("没有识别到语音"), L.T("可重新唤醒后再说一次"), detail, MicrophoneSettings);
        return new(L.T("语音助手暂不可用"), L.T("点击查看完整说明"), detail, SpeechSettings);
    }
}

internal sealed class VoiceRecognitionException(VoiceRecognitionProblem problem, Exception? inner = null)
    : InvalidOperationException(problem.Detail, inner)
{
    internal VoiceRecognitionProblem Problem { get; } = problem;
}
