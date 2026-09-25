using System;
using System.Drawing;
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
        private bool _disposed;

        /// <summary>左键单击 / 双击托盘图标。</summary>
        public event EventHandler? SettingsRequested;

        /// <summary>菜单里的"退出程序"。</summary>
        public event EventHandler? ExitRequested;

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
            if (_disposed || _icon != null) return;

            _menu = new ContextMenuStrip();
            var settingsItem = new ToolStripMenuItem("⚙️ 打开设置");
            settingsItem.Click += (_, __) => TriggerSettingsRequested();
            var exitItem = new ToolStripMenuItem("退出程序");
            exitItem.Click += (_, __) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _menu.Items.Add(settingsItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = tooltip,
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) TriggerSettingsRequested();
            };
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
