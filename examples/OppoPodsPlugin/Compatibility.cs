using L = AlphaIsland.PluginSdk.Localization.UiText;
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
        public CultureInfo CurrentCulture => L.Culture;
        public string GetString(string key, params object[] arguments)
            => arguments.Length == 0 ? key : string.Format(CurrentCulture, key, arguments);
        public string Anc_ModeAdaptive => L.T("自适应");
        public string Anc_ModeNoiseCancellation => L.T("降噪");
        public string Anc_ModeOff => L.T("关闭");
        public string Anc_ModeTransparency => L.T("通透");
        public string Anc_SubDeep => L.T("深度");
        public string Anc_SubLight => L.T("轻度");
        public string Anc_SubMedium => L.T("中度");
        public string Anc_SubSmart => L.T("智能");
        public string Battery_Left => L.T("左耳");
        public string Battery_Right => L.T("右耳");
        public string DeviceInfo_Button => L.T("按键");
        public string DeviceInfo_DoubleTap => L.T("双击");
        public string DeviceInfo_LongPress => L.T("长按");
        public string DeviceInfo_SingleTap => L.T("单击");
        public string DeviceInfo_SlideDown => L.T("向下滑动");
        public string DeviceInfo_SlideUp => L.T("向上滑动");
        public string DeviceInfo_TripleTap => L.T("三击");
        public string DeviceInfo_UnknownAction => L.T("未知动作");
        public string DeviceInfo_UnknownSide => L.T("未知侧");
        public string Device_Unrecognized => L.T("未识别型号");
        public string Eq_Default => L.T("默认");
        public string Eq_ModeIndex => L.T("预设 {0}");
        public string Status_ConnectedPlain => L.T("已连接");
        public string Status_Connecting => L.T("连接中");
        public string Status_CurrentDevice => L.T("本机");
        public string Status_DisconnectedPlain => L.T("未连接");
    }
}
