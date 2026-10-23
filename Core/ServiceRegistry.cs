using System;
using System.Collections.Generic;

namespace alpha_island.Core
{
    /// <summary>
    /// 服务 Id 表（阶段 3.3）。宿主、诊断页、后续新增的服务都从这里取常量，
    /// 避免 <c>EnsureStarted("volume")</c> 这类字面量散落各处拼错。
    /// <para>新增服务时：在这里加一个常量 + 中文名，然后在 <c>MainWindow.BuildServiceHost()</c> 里注册。</para>
    /// </summary>
    public static class IslandServiceIds
    {
        // ── 已有服务（阶段 0 前就在跑）──
        public const string Smtc = "smtc";
        public const string Spectrum = "spectrum";
        public const string Lyrics = "lyrics";
        public const string Clipboard = "clipboard";
        public const string FileTransfer = "filetransfer";
        public const string Volume = "volume";
        public const string Battery = "battery";
        public const string Bluetooth = "bluetooth";

        // ── 系统状态与助手服务──
        public const string Telemetry = "telemetry";
        public const string NetworkSpeed = "networkspeed";
        public const string MemoryMonitor = "memory";
        public const string PrivacyIndicator = "privacyindicator";
        public const string AiChat = "aichat";
        public const string AgentBridge = "agentbridge";

        // ── 每日流量（只读外部程序写的文件）──
        public const string TrafficMonitorLink = "trafficmonitor";

        // ── 第二批③：下载进度（浏览器 downloads 表 + Steam 清单，只读）──
        public const string DownloadMonitor = "downloadmonitor";
        public const string Cpu = "cpu";
        public const string HardwarePower = "hardwarepower";
        public const string DesktopNotifications = "desktopnotifications";
        public const string MessengerNotifications = "messengernotifications";
        public const string SessionEvents = "sessionevents";
        public const string GameActivity = "gameactivity";

        /// <summary>全部 Id（按注册顺序无关；用于自检与文档）。</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Smtc, Spectrum, Lyrics, Clipboard, FileTransfer, Volume, Battery, Bluetooth,
            Telemetry, NetworkSpeed, MemoryMonitor, PrivacyIndicator, AiChat, AgentBridge,
            TrafficMonitorLink, DownloadMonitor, Cpu, HardwarePower, DesktopNotifications, MessengerNotifications, SessionEvents, GameActivity,
        };

        /// <summary>Id → 中文显示名的兜底映射（服务自己的 <c>DisplayName</c> 优先）。</summary>
        public static string DisplayName(string id) => id switch
        {
            Smtc => "系统媒体（SMTC）",
            Spectrum => "WASAPI 频谱采集",
            Lyrics => "歌词服务",
            Clipboard => "剪切板与热键",
            FileTransfer => "文件中转站",
            Volume => "系统音量监听",
            Battery => "电池状态监听",
            Bluetooth => "蓝牙设备监听",
            Telemetry => "无线开关监听（Wi-Fi / 蓝牙）",
            NetworkSpeed => "网速与下载进度",
            MemoryMonitor => "内存占用采样",
            PrivacyIndicator => "麦克风摄像头指示灯",
            AiChat => "AI 对话",
            AgentBridge => "Agent 状态与审批",
            TrafficMonitorLink => "每日流量",
            DownloadMonitor => "下载进度（浏览器 / Steam）",
            Cpu => "CPU 占用采样",
            HardwarePower => "CPU＋GPU 硬件功耗",
            DesktopNotifications => "Windows 通知监听",
            MessengerNotifications => "QQ / 微信新消息提醒",
            SessionEvents => "锁屏与解锁事件",
            GameActivity => "游戏自动免打扰",
            _ => id,
        };
    }
}
