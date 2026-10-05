using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace alpha_island.Interop
{
    /// <summary>
    /// 系统托盘入口。用户明确要求："设置界面改为像程序一样在任务栏出现，
    /// 在启动时可以在托盘处打开设置，而不是右键打开设置，因为岛在后期需要用鼠标按键完成其他交互。"
    /// 所以左键单击 = 打开设置，右键才出菜单。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private NotifyIcon? _icon;
        private ContextMenuStrip? _menu;
        private ToolStripMenuItem? _memoryTrimItem;
        private bool _disposed;

        /// <summary>左键单击 / 双击托盘图标。</summary>
        public event EventHandler? SettingsRequested;

        /// <summary>菜单里的"退出程序"。</summary>
        public event EventHandler? ExitRequested;

        /// <summary>菜单里的"整理内存"（A 组②）。</summary>
        public event EventHandler? MemoryTrimRequested;

        private DateTime _lastSettingsRequestTime = DateTime.MinValue;

        private void TriggerSettingsRequested()
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            if ((now - _lastSettingsRequestTime).TotalMilliseconds < 350) return;
            _lastSettingsRequestTime = now;
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Show(string tooltip = "Alpha Island (灵动岛)")
        {
            Show(tooltip, icon: null);
        }

        /// <summary>
        /// 显示托盘图标。<paramref name="icon"/> 传 null 时按 <see cref="LoadAppIcon"/> 的兜底链取图标
        /// （C 组①：以前这里写死 <c>SystemIcons.Application</c>，现在是应用自己的多尺寸图标）。
        /// </summary>
        public void Show(string tooltip, Icon? icon)
        {
            if (_disposed || _icon != null) return;

            _menu = TrayMenuTheme.Create();
            var settingsItem = new ToolStripMenuItem("打开设置");
            settingsItem.Click += (_, __) => TriggerSettingsRequested();
            // A 组②：一键内存整理（只回收本进程工作集，免提权）。开关关闭时这一项变灰。
            _memoryTrimItem = new ToolStripMenuItem("整理内存");
            _memoryTrimItem.Click += (_, __) => MemoryTrimRequested?.Invoke(this, EventArgs.Empty);
            var exitItem = new ToolStripMenuItem("退出程序");
            exitItem.Click += (_, __) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _menu.Items.Add(settingsItem);
            _menu.Items.Add(_memoryTrimItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Icon = icon ?? LoadAppIcon(),
                Text = tooltip,
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) TriggerSettingsRequested();
            };
        }

        /// <summary>
        /// 托盘/窗口图标的三级兜底：
        /// ① <c>%LOCALAPPDATA%\alpha-island\icon.ico</c> —— 用户不重编译就能换图标；
        /// ② 程序内嵌的 <c>Assets\alpha-island.ico</c>（WPF Resource，<c>ApplicationIcon</c> 同步给 exe）；
        /// ③ exe 自己的 Win32 图标（<see cref="Icon.ExtractAssociatedIcon(string)"/>）；
        /// ④ 最后退回系统通用图标（保证托盘一定画得出来）。
        /// </summary>
        public static Icon? LoadAppIcon(int? size = null)
        {
            int px = size ?? DefaultIconSizePixels();

            try
            {
                string userIcon = Core.AppPaths.UserIconFile;
                if (File.Exists(userIcon))
                {
                    using var stream = File.OpenRead(userIcon);
                    return new Icon(stream, new Size(px, px));
                }
            }
            catch (Exception ex)
            {
                Core.IslandLog.Warn("Tray", "读取用户图标失败，改用内置图标: " + ex.Message);
            }

            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/alpha-island.ico"));
                if (resource?.Stream != null)
                {
                    using var stream = resource.Stream;
                    Core.IslandLog.Debug("Tray", $"托盘图标 = 内置 Assets/alpha-island.ico（{px}px）");
                    return new Icon(stream, new Size(px, px));
                }
            }
            catch (Exception ex)
            {
                Core.IslandLog.Warn("Tray", "加载内置图标失败: " + ex.Message);
            }

            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null) return extracted;
                }
            }
            catch (Exception ex)
            {
                Core.IslandLog.Warn("Tray", "从 exe 提取图标失败: " + ex.Message);
            }

            return SystemIcons.Application;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        /// <summary>
        /// 托盘图标要多少像素：125% DPI 下系统期望 20px（16px 会被拉糊），
        /// 所以按系统 DPI 算 16 * dpi/96，并与 <see cref="SystemInformation.SmallIconSize"/> 取较大者。
        /// </summary>
        private static int DefaultIconSizePixels()
        {
            int fallback = SystemInformation.SmallIconSize.Width > 0 ? SystemInformation.SmallIconSize.Width : 16;
            uint dpi = 0;
            try
            {
                dpi = GetDpiForSystem();
                if (dpi >= 96)
                {
                    int scaled = (int)Math.Round(16.0 * dpi / 96.0);
                    int result = Math.Max(fallback, Math.Min(32, scaled));
                    Core.IslandLog.Debug("Tray", $"图标尺寸 = {result}px（系统 dpi={dpi}，SmallIconSize={fallback}）");
                    return result;
                }
            }
            catch
            {
                // 老系统没有 GetDpiForSystem（Win10 1607 之前）→ 用 SmallIconSize
            }
            Core.IslandLog.Debug("Tray", $"图标尺寸 = {fallback}px（取 SmallIconSize；dpi 探测值 {dpi} 不可用）");
            return fallback;
        }

        /// <summary>
        /// 「整理内存」菜单项是否可用（跟着设置里的"一键内存整理"开关走）。
        /// 菜单项始终列出来（用户看得见入口），关闭时只是变灰，避免"菜单项时有时无"的困惑。
        /// </summary>
        public void SetMemoryTrimEnabled(bool enabled)
        {
            if (_memoryTrimItem != null) _memoryTrimItem.Enabled = enabled;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            _menu?.Dispose();
            _menu = null;
        }
    }
}
