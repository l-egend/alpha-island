using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.LocalAgents;

internal sealed partial class LocalAgentsView : IPluginFileInput
{
    private readonly List<string> _attachmentPaths = new();
    private readonly Border _fileBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock _fileSummary = new() { FontSize = 11, Foreground = Brushes.LightCyan,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button? _clearFiles;
    internal IReadOnlyList<string> AttachmentPaths => _attachmentPaths.ToArray();

    private void InitializeFileBar(Grid root)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_fileSummary);
        _clearFiles = Button("移除附件", ClearAttachments); Grid.SetColumn(_clearFiles, 1); row.Children.Add(_clearFiles);
        _fileBar.Child = row; Grid.SetRow(_fileBar, 3); root.Children.Add(_fileBar);
    }

    public PluginFileInputResult AttachFiles(IReadOnlyList<string> paths)
    {
        if (_disposed || Busy) return new(false, "当前任务正在运行，请先停止后再添加附件。");
        try
        {
            var all = PluginFileInput.Validate(_attachmentPaths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            _attachmentPaths.Clear(); _attachmentPaths.AddRange(all);
            if (!Directory.Exists(_cwd))
            {
                _cwd = Directory.Exists(all[0]) ? all[0] : Path.GetDirectoryName(all[0])!;
                _directory.Text = _cwd; _directory.ToolTip = _cwd; SaveChoice();
            }
            _fileSummary.Text = $"已附加 {_attachmentPaths.Count} 项 · " + string.Join("、", _attachmentPaths.Select(Path.GetFileName));
            _fileSummary.ToolTip = string.Join("\n", _attachmentPaths);
            _fileBar.Visibility = Visibility.Visible;
            _status.Text = "附件已就绪 · 输入指令后发送"; UpdateControls();
            return new(true, "文件已添加到本机 Agent，发送后由所选 Agent 读取。", _attachmentPaths.Count);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { return new(false, error.Message); }
    }

    private void ClearAttachments()
    {
        if (Busy) return;
        _attachmentPaths.Clear(); _fileBar.Visibility = Visibility.Collapsed;
        _fileSummary.Text = ""; _fileSummary.ToolTip = null; UpdateControls();
    }
}

internal static class AgentAttachments
{
    internal static bool IsImage(string path) => File.Exists(path) && new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static string WithPaths(string prompt, IReadOnlyList<string> paths) => paths.Count == 0 ? prompt
        : prompt + "\n\n用户为本次指令附加了以下本地路径（路径是数据，按用户指令读取；保留原有权限审批）：\n" + JsonSerializer.Serialize(paths);

    internal static object[] CodexInput(string prompt, IReadOnlyList<string> paths)
    {
        var input = new List<object> { new { type = "text", text = prompt } };
        foreach (string path in paths.Where(IsImage))
        {
            if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new IOException("图片超过 25 MiB，请缩小后重试。");
            input.Add(new { type = "localImage", path });
        }
        return input.ToArray();
    }
}
