using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void MessengerActivationChecks()
    {
        Check(MessengerIdentity.FromTrayName(" 微信") == MessengerClient.WeChat
            && MessengerIdentity.FromTrayName("\u200E微信") == MessengerClient.WeChat, "托盘应用名称的前置空白或方向标记不影响微信入口识别");
        var calls = new List<string>();
        bool ready = false;
        var operations = new MessengerActivationOperations(
            () => { calls.Add("native-show"); return true; },
            () => { calls.Add("tray"); ready = true; return true; },
            () => { calls.Add("client-start"); ready = true; return true; });
        bool opened = MessengerWindowReader.TryOpen(MessengerClient.WeChat, operations);
        Check(opened && ready && calls.SequenceEqual(new[] { "tray" }),
            "微信跳转执行应用自身的托盘恢复，不直接显示未初始化的隐藏窗口");
        calls.Clear();
        operations = new(
            () => { calls.Add("native-show"); return true; },
            () => { calls.Add("tray"); return false; },
            () => { calls.Add("client-start"); return true; });
        Check(MessengerWindowReader.TryOpen(MessengerClient.WeChat, operations)
            && calls.SequenceEqual(new[] { "tray", "client-start" }), "托盘入口暂不可用时交给微信自身启动入口，仍不强制显示 HWND");
        calls.Clear();
        operations = operations with { StartClient = () => { calls.Add("client-start"); return false; } };
        Check(!MessengerWindowReader.TryOpen(MessengerClient.WeChat, operations)
            && !calls.Contains("native-show"), "微信自身入口均失败时返回失败，不退回到造成假死的窗口显示方式");
        calls.Clear();
        operations = new(() => { calls.Add("native-show"); return true; }, () => { calls.Add("tray"); return true; }, () => false);
        Check(MessengerWindowReader.TryOpen(MessengerClient.QQ, operations)
            && calls.SequenceEqual(new[] { "tray" }), "QQ 通过自身托盘动作完成恢复，避免直接显示隐藏窗口造成假死");
    }
}
