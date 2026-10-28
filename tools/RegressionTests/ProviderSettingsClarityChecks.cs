using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static void ProviderSettingsClarityChecks(alpha_island.MainWindow window, string root)
    {
        string target = Path.Combine(root, "api-clarity-worker"); Directory.CreateDirectory(target);
        string source = Path.GetFullPath($"examples/ApiChatPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach (string file in Directory.GetFiles(source).Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json") || file.EndsWith("plugin.json")))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        var manifest = PluginManifest.Read(Path.Combine(target, "plugin.json"));
        using var session = new PluginProcessSession(window.Dispatcher);
        AwaitPlugin(session.StartAsync(new(target, manifest.Assembly, manifest.EntryType, manifest.Id,
            Path.Combine(root, "data"), new Dictionary<string, JsonElement>()), CancellationToken.None));
        var failures = new List<string>();
        void Expect(bool condition, string description)
        {
            Console.WriteLine((condition ? "PASS: " : "FAIL: ") + description);
            if (condition) _checks++; else failures.Add(description);
        }
        try
        {
            foreach (double scale in new[] { 1.25, 1.5, 2 })
            {
                var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new { Key = "$settings", Width = 480, Height = 650, Scale = scale }));
                var button = frame.Elements.Single(item => item.Name == "ManageAiHistoryButton");
                using var bytes = new MemoryStream(Convert.FromBase64String(button.Artwork));
                var image = BitmapFrame.Create(bytes, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Expect(image.PixelWidth >= Math.Ceiling(button.Width * scale) && image.PixelHeight >= Math.Ceiling(button.Height * scale),
                    $"{scale:P0} 缩放：真实插件按钮按目标像素密度输出，不放大低分辨率截图");
                var remote = new RemotePluginView("$settings", _ => Task.CompletedTask, error => throw error);
                remote.Apply(frame);
                Expect(remote.FindName("ManageAiHistoryButton") != null, $"{scale:P0} 缩放：宿主可以接收并显示高分辨率插件内容");
                if (scale == 2) SaveProviderPreview(remote, "api-settings-480-200pct", 480, frame.Height, scale);
                remote.Close();
            }
            foreach (int width in new[] { 414, 480, 560, 720 })
            {
                var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("$settings", width, 650)));
                var title = frame.Elements.First(item => item.Kind == "text" && item.Text == "AI 对话");
                var label = frame.Elements.Single(item => item.Kind == "text" && item.Text == "API Key");
                var editor = frame.Elements.Single(item => item.Name == "AiApiKeyBox");
                Expect(title.X >= 12, $"{width} DIP：插件标题保留内侧留白");
                Expect(label.Width >= 120 && (editor.Y >= label.Y + label.Height || editor.X >= label.X + 120),
                    $"{width} DIP：标签至少保留 120 DIP，窄窗口不被固定宽编辑器挤压");
                Expect(editor.X >= 12 && editor.X + editor.Width <= width - 12,
                    $"{width} DIP：编辑器与两侧边界保持间距");
                if (width == 720)
                {
                    var remote = new RemotePluginView("$settings", _ => Task.CompletedTask, error => throw error); remote.Apply(frame);
                    SaveProviderPreview(remote, "api-settings-720", width, frame.Height, 1); remote.Close();
                }
            }
            var editableFrame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("$settings", 720, 650)));
            var editableHost = new RemotePluginView("$settings", async command => { await session.Request<bool>("input", command); }, error => throw error);
            try
            {
                editableHost.Apply(editableFrame); editableHost.Measure(new System.Windows.Size(720, 650)); editableHost.Arrange(new Rect(0, 0, 720, 650)); editableHost.UpdateLayout();
                var model = (System.Windows.Controls.ComboBox)editableHost.FindName("AiModelBox");
                Expect(model.IsEditable, "真实宿主中的模型选择框支持手动输入");
                var editor = (System.Windows.Controls.TextBox)model.Template.FindName("PART_EditableTextBox", model);
                editor.Text = "manual-cross-process"; PumpAnimations(100);
                var updated = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("$settings", 720, 650)));
                Expect(updated.Elements.Single(item => item.Name == "AiModelBox").Text == "manual-cross-process", "手动输入跨进程传到插件并在刷新后保留");
            }
            finally { editableHost.Close(); }
        }
        finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true)); }
        Check(failures.Count == 0, "插件设置在不同缩放和宽度下清晰且布局完整");
    }

    private static void SaveProviderPreview(FrameworkElement content, string name, double width, double height, double scale)
    {
        var panel = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 28)), Child = content };
        panel.Measure(new System.Windows.Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(panel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.GetFullPath("bin/ServiceProviderAudit"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output); panel.Child = null;
    }
}
