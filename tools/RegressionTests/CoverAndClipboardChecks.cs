using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using alpha_island.Core;
using alpha_island.Interop;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;

internal static partial class Program
{
    private static void NativeClipboardReadChecks()
    {
        // 使用测试自己的 HGLOBAL；不打开、改写或清空用户的系统剪切板。
        using var api = new ClipboardReadFixture(Encoding.Unicode.GetBytes("中文 🎵\r\nsecond\0ignored"));
        Check(ClipboardTextReader.ReadText(api) == "中文 🎵\r\nsecond", "原生文本快照保留中文、补充字符和换行，并在终止符停止");
        Check(api.Closes == 1 && api.Locks == 1 && api.Unlocks == 1 && api.LockCount == 0,
            "读取后配对解锁、关闭且仍由数据提供方持有内存");
        api.Files = true;
        Check(ClipboardTextReader.ReadText(api) == null && api.Gets == 1 && api.Closes == 2,
            "附带文字的文件载荷不读取文本句柄，仍关闭剪切板");
        api.Files = false; api.Text = false;
        Check(ClipboardTextReader.ReadText(api) == null && api.Gets == 1 && api.Closes == 3, "图片或空载荷不会保留旧文本");
        api.Text = true; api.CanOpen = false;
        bool busy = false;
        try { ClipboardTextReader.ReadText(api); } catch (ExternalException) { busy = true; }
        Check(busy && api.Closes == 3, "剪切板被占用时进入可重试错误，不关闭别人的打开会话");
        api.CanOpen = true; api.FailGet = true;
        bool getFailed = false;
        try { ClipboardTextReader.ReadText(api); } catch (ExternalException) { getFailed = true; }
        Check(getFailed && api.Closes == 4 && api.Locks == 1, "延迟提供文本失败时关闭剪切板并保留重试机会");
        api.FailGet = false; api.FailLock = true;
        bool lockFailed = false;
        try { ClipboardTextReader.ReadText(api); } catch (ExternalException) { lockFailed = true; }
        Check(lockFailed && api.Closes == 5 && api.Unlocks == 1, "内存锁定失败时关闭剪切板而不解锁未获得的锁");
        api.FailLock = false;
        foreach (ulong size in new ulong[] { 0, 1, 3, ClipboardTextReader.MaxTextBytes + 2UL, ulong.MaxValue })
        {
            api.SizeOverride = size;
            Check(ClipboardTextReader.ReadText(api) == null && api.Locks == 2, $"异常文本大小 {size} 不触碰内存");
        }
        api.SizeOverride = null;
        using var unterminated = new ClipboardReadFixture(Encoding.Unicode.GetBytes("没有终止符"));
        Check(ClipboardTextReader.ReadText(unterminated) == null && unterminated.Unlocks == 1 && unterminated.Closes == 1,
            "无终止符文本不会越界，拒绝后仍释放锁与读取会话");
        using var empty = new ClipboardReadFixture(new byte[] { 0, 0 });
        Check(ClipboardTextReader.ReadText(empty) == "", "空文本快照可正常读取");
        int before = api.Closes;
        for (int i = 0; i < 1000; i++)
        {
            if (ClipboardTextReader.ReadText(api) != "中文 🎵\r\nsecond") throw new Exception("快照压力读取失败");
            if (i % 100 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        Check(api.Closes == before + 1000 && api.Unlocks == api.Locks - 1 && api.LockCount == 0,
            "连续 1000 次读取与垃圾回收不会遗留内存锁或延迟释放数据对象");
    }

    private sealed class ClipboardReadFixture : ClipboardTextReader.IReadApi, IDisposable
    {
        private readonly IntPtr _handle;
        public bool Files, FailGet, FailLock;
        public bool Text = true, CanOpen = true;
        public int Closes, Locks, Unlocks, Gets;
        public ulong? SizeOverride;
        public int LockCount => (int)(GlobalFlags(_handle) & 0xff);
        public ClipboardReadFixture(byte[] bytes)
        {
            _handle = GlobalAlloc(2, (UIntPtr)bytes.Length);
            if (_handle == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr pointer = NativeMethods.GlobalLock(_handle);
            if (pointer == IntPtr.Zero) { GlobalFree(_handle); throw new OutOfMemoryException(); }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { NativeMethods.GlobalUnlock(_handle); }
        }
        public bool Open() => CanOpen;
        public void Close() => Closes++;
        public bool HasFiles() => Files;
        public bool HasText() => Text;
        public IntPtr GetTextHandle() { Gets++; return FailGet ? IntPtr.Zero : _handle; }
        public ulong Size(IntPtr handle) => SizeOverride ?? NativeMethods.GlobalSize(handle).ToUInt64();
        public IntPtr Lock(IntPtr handle) { Locks++; return FailLock ? IntPtr.Zero : NativeMethods.GlobalLock(handle); }
        public void Unlock(IntPtr handle) { Unlocks++; NativeMethods.GlobalUnlock(handle); }
        public void Dispose() => GlobalFree(_handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint GlobalFlags(IntPtr handle);
    }

    private static void RoundedCoverChecks(alpha_island.MainWindow window, Grid root)
    {
        var mesh = CoverMesh.RoundedSquare(.60, .60 * 14 / 32);
        bool frontFacing = true;
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.TriangleIndices[i]];
            var b = mesh.Positions[mesh.TriangleIndices[i + 1]];
            var c = mesh.Positions[mesh.TriangleIndices[i + 2]];
            frontFacing &= Vector3D.CrossProduct(b - a, c - a).Z > 0;
        }
        Check(frontFacing && mesh.IsFrozen && mesh.Positions.All(p => Math.Abs(p.X) <= .600001 && Math.Abs(p.Y) <= .600001),
            "圆角封面网格三角面朝向镜头，边界正确且冻结后可安全共享");
        Check(mesh.Positions.Skip(1).All(p => Math.Abs(p.X) < .60 - 1e-6 || Math.Abs(p.Y) < .60 - 1e-6)
            && mesh.TextureCoordinates.All(p => p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1),
            "3D 网格四角真正切成弧线，贴图坐标保持完整画幅");
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 32, 32));
            draw.DrawRectangle(Brushes.Green, null, new Rect(32, 0, 32, 32));
            draw.DrawRectangle(Brushes.Blue, null, new Rect(0, 32, 32, 32));
            draw.DrawRectangle(Brushes.Purple, null, new Rect(32, 32, 32, 32));
        }
        var art = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); art.Render(visual); art.Freeze();
        PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Music);
        window.CardCoverBrush.ImageSource = art; window.PillCoverBrush.ImageSource = art;
        window.CardMediaPlaceholder.Visibility = Visibility.Collapsed;
        PrivateCall(window, "EnsureCover3DScene");
        PrivateCall(window, "SyncCover3DArt", art);
        byte[] Pixels(FrameworkElement element, int size)
        {
            root.UpdateLayout(); element.UpdateLayout();
            var drawing = new DrawingVisual();
            using (var draw = drawing.RenderOpen())
                draw.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var bytes = new byte[size * size * 4]; bitmap.CopyPixels(bytes, size * 4, 0); return bytes;
        }
        bool Rounded(byte[] bytes, int size)
            => new[] { 0, size - 1, size * (size - 1), size * size - 1 }.All(i => bytes[i * 4 + 3] == 0)
                && bytes[(size / 2 * size + size / 2) * 4 + 3] > 200;
        try
        {
            PrivateCall(window, "SetCover3DActive", false, "隔离测试 2D 圆角封面");
            Check(Rounded(Pixels(window.CardCoverHost, 64), 64), "2D 卡片封面四角透明，中央保持显示");
            PrivateCall(window, "SetCover3DActive", true, "隔离测试 3D 圆角封面");
            var pixels = Pixels(window.CardCoverHost, 64);
            Check(Rounded(pixels, 64), "开启 3D 后封面仍是圆角矩形");
            int topLeft = (16 * 64 + 16) * 4, bottomLeft = (48 * 64 + 16) * 4;
            Check(pixels[topLeft + 2] > pixels[topLeft] && pixels[bottomLeft] > pixels[bottomLeft + 2],
                "3D 封面圆角贴图方向正确，没有上下翻转");
            SaveThemePreview(window.CardCoverHost, "docs/screens/rounded-cover-3d.png");
            // 没有真实媒体会话时胶囊会被隐藏；单独挂载真实 XAML 封面验证其像素轮廓。
            var parent = (Panel)VisualTreeHelper.GetParent(window.PillCoverHost);
            int index = parent.Children.IndexOf(window.PillCoverHost);
            parent.Children.Remove(window.PillCoverHost); root.Children.Add(window.PillCoverHost);
            try
            {
                var pillPixels = Pixels(window.PillCoverHost, 32);
                SaveThemePreview(window.PillCoverHost, "docs/screens/rounded-cover-pill.png");
                Check(window.PillCoverHost.CornerRadius.TopLeft == 8 && Rounded(pillPixels, 32),
                    "紧凑音乐封面同样采用圆角矩形");
            }
            finally { root.Children.Remove(window.PillCoverHost); parent.Children.Insert(index, window.PillCoverHost); }

            // Compact media moved to SemanticIcon.Source; the card/pill borders do not
            // clip this independent cover path. Drive the real compact-island wiring.
            var oldService = AgentField<alpha_island.Services.SmtcService?>(window, "_smtc");
            using var compactService = new alpha_island.Services.SmtcService(window.Dispatcher);
            var iconParent = (Panel)VisualTreeHelper.GetParent(window.SideBubbleIcon);
            int iconIndex = iconParent.Children.IndexOf(window.SideBubbleIcon);
            var oldSource = window.SideBubbleIcon.Source;
            var oldShape = window.SideBubbleIcon.Shape;
            try
            {
                SetAgentField(window, "_smtc", compactService);
                compactService.SetDebugNowPlaying(new alpha_island.Services.NowPlayingInfo
                {
                    Title = "圆角媒体缩略图回归", SourceApp = "cloudmusic.exe", SessionId = "rounded-compact",
                    State = alpha_island.Services.PlaybackState.Playing, Artwork = art
                });
                PrivateCall(window, "UpdateCompactIslandContent", "music");
                PrivateCall(window, "ShowSideBubbleHint", "\uE769", "媒体播放器", "music");
                iconParent.Children.Remove(window.SideBubbleIcon); root.Children.Add(window.SideBubbleIcon); PumpAnimations(30);
                var compactPixels = Pixels(window.SideBubbleIcon, 24);
                Check(compactPixels[3] == 0
                    && compactPixels[(12 * 24 + 12) * 4 + 3] > 200,
                    "真实旁侧媒体封面也裁切为圆角矩形，不因公共动态图标路径再次变成直角");
                SaveThemePreview(window.SideBubbleIcon, "docs/screens/rounded-cover-compact.png");
            }
            finally
            {
                root.Children.Remove(window.SideBubbleIcon); iconParent.Children.Insert(iconIndex, window.SideBubbleIcon);
                PrivateCall(window, "HideSideBubbleHint", true);
                window.SideBubbleIcon.Source = oldSource; window.SideBubbleIcon.Shape = oldShape;
                SetAgentField(window, "_smtc", oldService);
            }
        }
        finally
        {
            window.CardCoverBrush.ImageSource = null; window.PillCoverBrush.ImageSource = null;
            PrivateCall(window, "SyncCover3DArt", (object?)null); PrivateCall(window, "ApplyCover3DMode");
            window.CardMediaPlaceholder.Visibility = Visibility.Visible;
            PrivateCall(window, "CollapseToBar", true); root.UpdateLayout();
        }
    }
}
