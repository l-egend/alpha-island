using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace alpha_island.Interop
{
    /// <summary>
    /// 真实背景模糊 —— 液态玻璃真正的"底色"。
    ///
    /// 这是一个**独立于 WPF 的 Win32 窗口**，带 <c>WS_EX_NOREDIRECTIONBITMAP</c>，
    /// 内容由 <c>Windows.UI.Composition</c> 的 <c>HostBackdropBrush</c> 提供：
    /// DWM 真的把窗口背后的桌面模糊了，不是自己抓屏画的假模糊。
    ///
    /// 为什么必须开第二个窗口：WPF 的 <c>AllowsTransparency="True"</c> 会给窗口加
    /// <c>WS_EX_LAYERED</c>，而 <c>WS_EX_LAYERED</c> 与系统 backdrop 机制
    /// **结构性互斥** —— 单窗口不可能同时做到"逐像素透明"和"真实背景模糊"。
    /// 所以：玻璃窗在下（本类），现有的分层 WPF 窗在上做 overlay，XAML 完全不用动。
    ///
    /// 四步缺一不可（每一步漏掉的表现都在注释里标了）：
    ///   1. 创建时就要带 <c>WS_EX_NOREDIRECTIONBITMAP</c>（事后 SetWindowLong 静默失败）；
    ///   2. 先 <c>CreateDispatcherQueueController</c> 再 <c>new Compositor()</c>（否则 0x80070005）；
    ///   3. <c>ICompositorDesktopInterop.CreateDesktopWindowTarget</c> + HostBackdropBrush + 圆角裁剪；
    ///   4. <c>DwmSetWindowAttribute(hwnd, 38, 3)</c> —— 少了这句窗口就是**全黑**。
    ///
    /// 已知限制：多显示器混合 DPI 场景未覆盖（本机 100%，DIP == 像素）。
    /// </summary>
    public sealed class BackdropWindow : IDisposable
    {
        // ------------------------------ Win32 ------------------------------
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
            int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
            int ellipseW, int ellipseH);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate BackdropWndProcDelegate = BackdropWndProc;
        private static IntPtr _wndProcPtr;
        private static bool _classRegistered;
        private static readonly object _classLock = new();
        private const string BackdropClassName = "AlphaIslandBackdropClass";

        private static IntPtr BackdropWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_ERASEBKGND = 0x0014;
            const uint WM_PAINT = 0x000F;
            if (uMsg == WM_ERASEBKGND)
            {
                // 彻底拦截系统背景擦除，绝不让 GDI 绘制白色或黑色色块！
                return (IntPtr)1;
            }
            if (uMsg == WM_PAINT)
            {
                return DefWindowProcW(hWnd, uMsg, wParam, lParam);
            }
            return DefWindowProcW(hWnd, uMsg, wParam, lParam);
        }

        private static void EnsureClassRegistered()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(BackdropWndProcDelegate);
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = _wndProcPtr,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = GetModuleHandleW(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero, // NULL 画刷，绝对不让系统默认擦除为白色！
                    lpszMenuName = null,
                    lpszClassName = BackdropClassName,
                    hIconSm = IntPtr.Zero
                };
                RegisterClassExW(ref wc);
                _classRegistered = true;
            }
        }

        // ------------------------ CoreMessaging / Composition ------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int dwSize;
            public int threadType;    // 2 = DQTYPE_THREAD_CURRENT
            public int apartmentType; // 2 = DQTAT_COM_STA
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

        [ComImport]
        [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
        }

        /// <summary>CreateDispatcherQueueController 每线程只能成功一次；失败是正常的，忽略即可。</summary>
        private static void EnsureDispatcherQueue()
        {
            var o = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                threadType = 2,
                apartmentType = 2
            };
            try { CreateDispatcherQueueController(o, out _); }
            catch { /* 已经有队列了 */ }
        }

        // ------------------------------ 状态 ------------------------------
        /// <summary>诊断开关：ALPHA_ISLAND_GLASS = smallclip | noDwm | (空)。</summary>
        private static readonly string GlassMode =
            Environment.GetEnvironmentVariable("ALPHA_ISLAND_GLASS") ?? string.Empty;

        /// <summary>诊断开关可以叠加，用逗号分隔，如 "noDwm,abs"。</summary>
        private static bool Has(string flag) =>
            GlassMode.IndexOf(flag, StringComparison.OrdinalIgnoreCase) >= 0;

        private static readonly object LogGate = new object();

        internal static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "alpha-island");
                Directory.CreateDirectory(dir);
                lock (LogGate)
                {
                    File.AppendAllText(Path.Combine(dir, "glass.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* 日志失败不影响功能 */ }
        }

        private IntPtr _hwnd;
        private int _x, _y, _w, _h;

        private Windows.UI.Composition.Compositor? _compositor;
        private Windows.UI.Composition.Desktop.DesktopWindowTarget? _target;
        private Windows.UI.Composition.ContainerVisual? _root;
        private Windows.UI.Composition.SpriteVisual? _sprite;
        private Windows.UI.Composition.CompositionRoundedRectangleGeometry? _geometry;

        /// <summary>四步全部成功（否则界面应该退回纯渐变玻璃）。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>创建失败的原因，仅用于日志/排障。</summary>
        public string? Failure { get; private set; }

        public IntPtr Handle => _hwnd;

        private bool _visible = true;

        public static BackdropWindow? TryCreate()
        {
            var b = new BackdropWindow();
            return b.TryCreateCore() ? b : null;
        }

        /// <summary>
        /// 读 <c>HKCU\...\Themes\Personalize\EnableTransparency</c>。
        /// 读不到时按「开」处理（宁可让 DWM 自己决定，也不要凭空降级）。
        /// 公开给 <see cref="Core.CapabilityProbe"/> 复用，不要在别处再写一份。
        /// </summary>
        public static bool SystemTransparencyEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? v = key?.GetValue("EnableTransparency");
                if (v == null) return true;
                return Convert.ToInt32(v) != 0;
            }
            catch
            {
                return true;
            }
        }

        private bool TryCreateCore()
        {
            // 系统「透明效果」关掉时 DWM 根本不提供 backdrop，
            // 而 WS_EX_NOREDIRECTIONBITMAP 的窗口没有内容就是**纯黑**。
            // 所以先探一次，关着就直接判定不可用、退回纯渐变玻璃。
            if (!SystemTransparencyEnabled())
            {
                Failure = "系统「透明效果」已关闭（设置 → 个性化 → 颜色），DWM 不提供 backdrop";
                return false;
            }

            try
            {
                EnsureClassRegistered();

                _hwnd = CreateWindowExW(
                    WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
                    WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
                    BackdropClassName, "AlphaIslandBackdrop",
                    WS_POPUP | WS_VISIBLE,
                    0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    Failure = "CreateWindowExW 失败 err=" + Marshal.GetLastWin32Error();
                    return false;
                }

                EnsureDispatcherQueue();

                _compositor = new Windows.UI.Composition.Compositor();

                IntPtr thisPtr = ((WinRT.IWinRTObject)_compositor).NativeObject.ThisPtr;
                var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
                int hr = Marshal.QueryInterface(thisPtr, ref iid, out IntPtr ppv);
                if (hr != 0)
                {
                    Failure = "QueryInterface(ICompositorDesktopInterop) hr=0x" + hr.ToString("X8");
                    return false;
                }

                var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(ppv);
                interop.CreateDesktopWindowTarget(_hwnd, true, out IntPtr targetPtr);
                _target = WinRT.MarshalInspectable<Windows.UI.Composition.Desktop.DesktopWindowTarget>.FromAbi(targetPtr);

                _root = _compositor.CreateContainerVisual();
                _sprite = _compositor.CreateSpriteVisual();

                if (Has("abs"))
                {
                    // 诊断：改用显式 Size，排除 RelativeSizeAdjustment 导致精灵尺寸为 0 的可能
                    Log("abs: 使用显式 Size，不设 RelativeSizeAdjustment");
                }
                else
                {
                    _root.RelativeSizeAdjustment = new Vector2(1f, 1f);
                    _sprite.RelativeSizeAdjustment = new Vector2(1f, 1f);
                }

                _target.Root = _root;
                _geometry = _compositor.CreateRoundedRectangleGeometry();
                _geometry.Size = new Vector2(10f, 10f);
                _geometry.CornerRadius = new Vector2(5f, 5f);
                _sprite.Clip = _compositor.CreateGeometricClip(_geometry);
                _sprite.Brush = _compositor.CreateHostBackdropBrush();
                _root.Children.InsertAtTop(_sprite);

                // 诊断用：把裁剪几何钉死在一个小方块上，用来判断"模糊到底是谁产生的"。
                // 若模糊区跟着缩小 → 模糊来自 Composition 精灵的几何裁剪；
                // 若模糊区仍等于整个窗口 → 模糊来自 DWM 的窗口级 backdrop，裁剪无效。
                if (Has("smallclip"))
                {
                    _geometry.Offset = new Vector2(0f, 0f);
                    _geometry.Size = new Vector2(100f, 100f);
                    _geometry.CornerRadius = new Vector2(50f, 50f);
                    Log("smallclip: geometry pinned to 100x100 @0,0");
                }

                if (!Has("noDwm"))
                {
                    int darkMode = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE (强制深色模式，绝不允许 DWM 渲染亮色发白亚克力)
                    DwmSetWindowAttribute(_hwnd, 20, ref darkMode, sizeof(int));

                    int transient = 3; // DWMSBT_TRANSIENTWINDOW
                    int hrDwm = DwmSetWindowAttribute(_hwnd, 38, ref transient, sizeof(int));
                    if (hrDwm != 0)
                    {
                        Failure = "DwmSetWindowAttribute(38,3) hr=0x" + hrDwm.ToString("X8") + "（模糊会全黑）";
                        return false;
                    }
                }
                else
                {
                    Log("noDwm: 跳过 DwmSetWindowAttribute(38,3)");
                }

                IsAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>把玻璃窗放到 overlay 的正下方（两个窗口都是 Topmost，靠这个定 z 序）。</summary>
        public void PlaceBelow(IntPtr overlayHwnd)
        {
            if (_hwnd == IntPtr.Zero || overlayHwnd == IntPtr.Zero) return;
            SetWindowPos(_hwnd, overlayHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>动态调整毛玻璃精灵层的通透度（0.05 ~ 1.0）。</summary>
        public void SetOpacity(double opacity)
        {
            if (_sprite != null)
            {
                _sprite.Opacity = (float)Math.Clamp(opacity, 0.05, 1.0);
            }
        }

        /// <summary>设定窗口位置与尺寸。只在形态**一次性切换**时调，别每帧调（4~6.7ms/帧）。</summary>
        public void SetBounds(int x, int y, int w, int h)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (x == _x && y == _y && w == _w && h == _h) return;
            _x = x; _y = y; _w = w; _h = h;
            if (Has("abs"))
            {
                var size = new Vector2(w, h);
                if (_root != null) _root.Size = size;
                if (_sprite != null) _sprite.Size = size;
            }
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private double _lastRgnX = -999, _lastRgnY = -999, _lastRgnW = -999, _lastRgnH = -999, _lastRgnR = -999;

        /// <summary>
        /// 只更新圆角裁剪几何（岛体相对玻璃窗的位置/大小/圆角）。
        /// 这是纯 Composition 侧的属性写入，比改窗口尺寸便宜一个数量级，
        /// 所以连续形变就靠它 —— 玻璃窗本身固定不动。
        /// </summary>
        public void SetIsland(double x, double y, double w, double h, double cornerRadius)
        {
            var g = _geometry;
            if (g == null || w < 1 || h < 1) return;
            if (Has("smallclip")) return;   // 诊断：几何钉死，忽略真实岛体

            float r = (float)Math.Max(0, Math.Min(cornerRadius, Math.Min(w, h) / 2.0));
            g.Offset = new Vector2((float)x, (float)y);
            g.Size = new Vector2((float)w, (float)h);
            g.CornerRadius = new Vector2(r, r);

            // 关键优化：SetWindowRgn 开销巨大（每次重构 GDI 区域树并强制排队同步绘图），
            // 且 Composition 侧已有圆角几何裁剪。因此只在几何尺寸变化 >= 3 像素时才同步 GDI 窗口区域，
            // 并且 redraw 参数传 false，杜绝无谓的 GDI 强制重绘与白色闪烁！
            if (Math.Abs(w - _lastRgnW) < 3.0 && Math.Abs(h - _lastRgnH) < 3.0 &&
                Math.Abs(x - _lastRgnX) < 3.0 && Math.Abs(y - _lastRgnY) < 3.0 &&
                Math.Abs(r - _lastRgnR) < 2.0)
            {
                return;
            }

            _lastRgnX = x; _lastRgnY = y; _lastRgnW = w; _lastRgnH = h; _lastRgnR = r;

            IntPtr rgn = CreateRoundRectRgn(
                (int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Round(x + w) + 1, (int)Math.Round(y + h) + 1,
                (int)Math.Round(r * 2), (int)Math.Round(r * 2));
            if (rgn != IntPtr.Zero)
            {
                SetWindowRgn(_hwnd, rgn, false);
            }

            if (!_loggedIsland)
            {
                _loggedIsland = true;
                Log($"SetIsland first: x={x:F1} y={y:F1} w={w:F1} h={h:F1} r={r:F1} " +
                    $"rgn={(rgn != IntPtr.Zero ? "ok" : "null")} " +
                    $"win={_w}x{_h}@{_x},{_y}");
            }
        }

        private bool _loggedIsland;

        public void SetVisible(bool visible)
        {
            if (_hwnd == IntPtr.Zero || visible == _visible) return;
            _visible = visible;
            if (!visible)
            {
                // 隐藏时把 region 收掉，避免残留的圆角形状在下次显示前闪一下
                IntPtr empty = CreateRectRgn(0, 0, 0, 0);
                if (empty != IntPtr.Zero) SetWindowRgn(_hwnd, empty, false);
                _lastRgnW = -999;
            }
            ShowWindow(_hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            try { _compositor?.Dispose(); } catch { }
            _compositor = null;
            _target = null;
            _root = null;
            _sprite = null;
            _geometry = null;
            IsAvailable = false;
        }
    }
}
