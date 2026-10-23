using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Conversations;
using alpha_island.Services;
using AlphaIsland.Plugins.ApiChat;
using AgentRunState = alpha_island.Core.AgentRunState;

internal static partial class Program
{
    private static void ResidentMemoryChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var failures = new List<Exception>();
        void Case(Action action) { try { action(); } catch (Exception e) { failures.Add(e); Console.WriteLine("FAIL: " + e.Message); } }
        string directory = Path.Combine(testRoot, "resident-prefs"); Directory.CreateDirectory(directory);
        string download = Path.Combine(directory, "files"); Directory.CreateDirectory(download);
        string prefs = Path.Combine(directory, "Preferences");
        File.WriteAllText(prefs, JsonSerializer.Serialize(new { padding = new string('x', 500_000), download = new { default_directory = download } }));
        var monitor = new DownloadMonitorService();
        var read = typeof(DownloadMonitorService).GetMethod("TryReadDownloadDirectory", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
        string? Read() => (string?)read.Invoke(read.IsStatic ? null : monitor, [directory]);
        Check(Read() == download, "浏览器配置探针读取真实自定义下载目录");
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 12; i++) _ = Read();
        long preferencesBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine("Unchanged preferences allocations: " + preferencesBytes);
        Case(() => Check(preferencesBytes < 256_000, "配置未变化时连续下载采样不再反复分配整份大型 Preferences"));
        string next = Path.Combine(directory, "next"); Directory.CreateDirectory(next);
        File.WriteAllText(prefs, JsonSerializer.Serialize(new { download = new { default_directory = next } }));
        Check(Read() == next, "更改浏览器下载目录后下一次采样立即生效");
        File.WriteAllText(prefs, "{"); Check(Read() == null, "浏览器正在改写的无效配置不会变成有效缓存");
        File.WriteAllText(prefs, "[]"); Check(Read() == null, "非对象 JSON 根节点安全回退为默认下载目录");
        File.WriteAllText(prefs, "{\"download\":[]}"); Check(Read() == null, "类型错误的下载字段安全回退，不中断采样");
        File.WriteAllText(prefs, JsonSerializer.Serialize(new { download = new { default_directory = download } }));
        Check(Read() == download, "配置恢复可读后能正常重试");
        Directory.Delete(download); Check(Read() == null, "缓存命中也不把已删除的目录继续上报为可用");
        Directory.CreateDirectory(download); Check(Read() == download, "目录重新可用时不需要改写浏览器配置");

        var border = new System.Windows.Controls.Border { Width = 360, Height = 96, Background = new SolidColorBrush(Colors.Gray) };
        root.Children.Add(border); root.UpdateLayout();
        try
        {
            var shape = new LiquidIslandShape(); shape.Update(new(360, 96, 0, new CornerRadius(48)));
            var overlap = new GeometryGroup(); overlap.Children.Add(shape.Geometry.Clone());
            overlap.Transform = new TranslateTransform(-320, 0);
            LiquidMaterialOverlap.Apply(border, overlap);
            var drawing = (DrawingGroup)((DrawingBrush)border.Background).Drawing;
            var clip = drawing.ClipGeometry; _ = clip.Bounds;
            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++) _ = clip.Bounds;
            long geometryBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine("Stable liquid geometry allocations: " + geometryBytes);
            Case(() => Check(geometryBytes < 32_000, "静止液态材质的重复边界查询不再重建布尔路径"));
            Check(!clip.FillContains(new System.Windows.Point(10, 48)) && clip.FillContains(new System.Windows.Point(180, 48)), "优化后的液态材质仍只剔除重叠区域");
            LiquidMaterialOverlap.Apply(border, null);
            Check(border.Background is SolidColorBrush, "液态连接结束后恢复原有材质和背景绑定");
        }
        finally { root.Children.Remove(border); }

        var icon = new SemanticIcon();
        try
        {
            foreach (string source in new[] { "codex", "antigravity", "deepseek-harness", "claude-code" })
                foreach (var state in new[] { AgentRunState.Idle, AgentRunState.Thinking, AgentRunState.Streaming, AgentRunState.ToolWait, AgentRunState.Error })
                    AgentAvatarMotion.Configure(icon, source, state, null);
            var clips = (IDictionary)typeof(AgentAvatarMotion).GetField("Clips", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Console.WriteLine("Cached avatar clips: " + clips.Count);
            Case(() => Check(clips.Count <= 8, "跨平台多次切换状态后，已解码助手动画缓存保持有界"));
            var bitmaps = new HashSet<BitmapSource>();
            foreach (object clip in clips.Values)
                foreach (var frame in (BitmapSource[])clip.GetType().GetProperty("Frames")!.GetValue(clip)!)
                    bitmaps.Add(frame is CroppedBitmap crop ? crop.Source : frame);
            long pixels = bitmaps.Sum(bitmap => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
            Check(pixels <= 32L * 1024 * 1024, "缓存实际引用的解码图像保持在 32 MiB 预算内");
            Check(icon.Source is BitmapSource { PixelWidth: 192 }, "动画缓存优化保持原有清晰度和头像尺寸");
        }
        finally { AgentAvatarMotion.Release(icon); }

        string chats = Path.Combine(testRoot, "resident-chats");
        var store = new ChatSessionStore(chats);
        var history = Enumerable.Range(0, 401).Select(i => new ChatMessage(i % 2 == 0 ? ChatMessage.RoleUser : ChatMessage.RoleAssistant, "历史消息 " + i)).ToArray();
        store.Save("resident", history);
        var view = new ApiChatView(new ConversationPluginFixture(new IslandOptions(), chats));
        try
        {
            SetAgentField(view, "_chatSessions", store); SetAgentField(view, "_aiSessionId", "resident");
            PrivateCall(view, "LoadAiHistory");
            var messages = AgentField<List<ChatMessage>>(view, "_aiMessages");
            Case(() => Check(messages.Count <= 100, "长对话只把最近 100 条保留在当前助手内存中"));
            messages.Add(new(ChatMessage.RoleAssistant, "新消息")); PrivateCall(view, "SaveAiHistory");
            var saved = store.Load("resident");
            Check(saved.Count == 402 && saved[0].Content == "历史消息 0" && saved[^1].Content == "新消息", "驻留窗口裁剪后保存新消息仍保留完整早期历史");
            messages.RemoveAt(messages.Count - 1); PrivateCall(view, "SaveAiHistory");
            Check(store.Load("resident").Count == 401, "撤销最后一条消息时保持早期历史和逻辑偏移一致");
            store.EditMessage("resident", 0, "修改后的早期消息");
            PrivateCall(view, "LoadAiHistory"); messages.Add(new(ChatMessage.RoleAssistant, "新回复")); PrivateCall(view, "SaveAiHistory");
            Check(store.Load("resident")[0].Content == "修改后的早期消息", "从历史窗口编辑早期消息后不会被驻留窗口覆盖");
            var bubbles = (StackPanel)view.FindName("AiBubbleStack");
            for (int i = 0; i < 60; i++) PrivateCall(view, "AddAiBubble", "连续气泡 " + i, false);
            Case(() => Check(bubbles.Children.Count <= 40, "连续新增消息时岛上的 WPF 气泡也始终限制为 40 条"));
            store.Save("resident", [new(ChatMessage.RoleUser, "外部更新")]);
            PrivateCall(view, "SaveAiHistory");
            Check(store.Load("resident") is [{ Content: "外部更新" }], "历史前缀不再存在时拒绝用陈旧窗口覆盖文件");
            PrivateCall(view, "AiClearButton_Click", null, new RoutedEventArgs());
            Check(store.Load("resident").Count == 0, "清空对话不会让磁盘前缀记录重新出现");
        }
        finally { view.Dispose(); }
        Check(!System.Runtime.GCSettings.IsServerGC, "正式测试宿主运行 Workstation GC");
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
