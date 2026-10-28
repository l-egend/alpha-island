using System.Windows;
using alpha_island.Core;
using alpha_island.Plugins;

namespace alpha_island;

public partial class MainWindow
{
    private async void SendTransferFilesToAgent_Click(object sender, RoutedEventArgs e) => await SendTransferFilesToAgentAsync();

    private async Task<bool> SendTransferFilesToAgentAsync()
    {
        if (_exiting || GameDoNotDisturbActive || _fileTransfer?.Count is not > 0) return false;
        const string page = "plugin:ai.local-agents/tasks";
        if (_plugins == null || !PluginHost.IsEnabled(_options, "ai.local-agents"))
        {
            ShowSystemNotify("\uE99A", "请先启用本机 Agent", "在设置 → 插件中开启本机 Agent，再发送文件。", "本机 Agent");
            return false;
        }
        SendTransferFilesToAgentButton.IsEnabled = false;
        try
        {
            var result = await _plugins.AttachFilesAsync(page, _fileTransfer.Items.Select(item => item.FullPath).ToArray());
            return await Dispatcher.InvokeAsync(() =>
            {
                if (_exiting) return false;
                if (!result.Accepted)
                {
                    ShowSystemNotify("\uE99A", "文件暂未送达", result.Message, "本机 Agent"); return false;
                }
                SelectComponent(page);
                return _pages.ActiveId == page;
            });
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => { if (!_exiting) SendTransferFilesToAgentButton.IsEnabled = true; });
        }
    }
}
