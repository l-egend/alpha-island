using System.Globalization;
using System.ComponentModel;

namespace OppoPodsManager
{
    // Routes protocol diagnostics to the plugin host without the original app's global state.
    internal static class Log
    {
        internal static Action<string>? Sink;
        public static void D(string area, string message) { /* Verbose frames are intentionally not persisted. */ }
        public static void Ex(string area, string message, Exception error)
            => Sink?.Invoke($"{area}: {message} · {DescribeException(error)}");
        public static void Result(string area, string operation, bool success, string? detail = null)
        { if (!success) Sink?.Invoke($"{area}: {operation} · {detail}"); }
        public static string DescribeException(Exception error) => error.GetBaseException().Message;
        public static string DescribeWsaOrWin32(int code) => new Win32Exception(code).Message;
    }
}

namespace OppoPodsManager.Localization
{
    // The vendored model loader uses keys from the original localization service.
    internal sealed class LanguageManager
    {
        public static LanguageManager Instance { get; } = new();
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("zh-CN");
        public string GetString(string key, params object[] arguments)
            => arguments.Length == 0 ? key : string.Format(CurrentCulture, key, arguments);
        public string Anc_ModeAdaptive => "自适应";
        public string Anc_ModeNoiseCancellation => "降噪";
        public string Anc_ModeOff => "关闭";
        public string Anc_ModeTransparency => "通透";
        public string Anc_SubDeep => "深度";
        public string Anc_SubLight => "轻度";
        public string Anc_SubMedium => "中度";
        public string Anc_SubSmart => "智能";
        public string Battery_Left => "左耳";
        public string Battery_Right => "右耳";
        public string DeviceInfo_Button => "按键";
        public string DeviceInfo_DoubleTap => "双击";
        public string DeviceInfo_LongPress => "长按";
        public string DeviceInfo_SingleTap => "单击";
        public string DeviceInfo_SlideDown => "向下滑动";
        public string DeviceInfo_SlideUp => "向上滑动";
        public string DeviceInfo_TripleTap => "三击";
        public string DeviceInfo_UnknownAction => "未知动作";
        public string DeviceInfo_UnknownSide => "未知侧";
        public string Device_Unrecognized => "未识别型号";
        public string Eq_Default => "默认";
        public string Eq_ModeIndex => "预设 {0}";
        public string Status_ConnectedPlain => "已连接";
        public string Status_Connecting => "连接中";
        public string Status_CurrentDevice => "本机";
        public string Status_DisconnectedPlain => "未连接";
    }
}
