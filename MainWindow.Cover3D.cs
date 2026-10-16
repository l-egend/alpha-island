using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using alpha_island.Animation;
using alpha_island.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using ImageBrush = System.Windows.Media.ImageBrush;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Stretch = System.Windows.Media.Stretch;
using Typeface = System.Windows.Media.Typeface;
using Visibility = System.Windows.Visibility;

namespace alpha_island
{
    /// <summary>
    /// 卡片封面槽的「3D 封面」模式（C 组④ ②）。
    ///
    /// 默认关闭；开启后卡片封面位置换成一块 <see cref="Viewport3D"/> 里的立体卡片，
    /// 换曲时从 -90°（侧对镜头、看不见）转到 0°，即"封面翻转入场"。
    ///
    /// 三个刻意的取舍：
    ///   * <b>不用 <c>Viewport2DVisual3D</c></b>：它要求把 2D 元素作为材质，软件渲染路径不支持，
    ///     而岛体是分层窗口（<c>AllowsTransparency</c>）+ DWM backdrop，整窗有可能退回软件渲染
    ///     → 这里用最朴素的 <see cref="DiffuseMaterial"/> + <see cref="ImageBrush"/>，两条路径都吃得下。
    ///   * <b>翻转只走正面</b>：绕 Y 轴 -90° → 0°，背面永远不入镜，所以不设 <c>BackMaterial</c>。
    ///   * <b>默认不写回 options</b>：渲染层级不够时只拒本次会话，不动用户设置 —— 换了显卡就能用。
    ///
    /// 还有一个"探针"（<see cref="ProbeCover3DRendering"/>）：把 Viewport3D 渲到
    /// <see cref="RenderTargetBitmap"/> 数一下非透明像素，0 像素就说明这个窗口里 3D 根本画不出来，
    /// 立刻退回 2D。这个会话没法截图（<c>CopyFromScreen</c> 拿旧帧、<c>PrintWindow</c> 对分层窗口
    /// 返回相同字节），探针是唯一能回答"到底画出来没有"的手段。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>翻转时长。和卡片翻页的 260ms 分开：封面是"换曲"的视觉锚点，稍慢一点更稳。</summary>
        private const double Cover3DFlipMs = 420;

        /// <summary>3D 卡片半边长（世界单位）。相机在 z=1.55、FOV 45°，可见高度约 1.284 → 卡片占 ~93%。</summary>
        private const double Cover3DHalfSize = 0.60;

        /// <summary>相机到卡片的距离。</summary>
        private const double Cover3DCameraZ = 1.55;

        private bool _cover3DActive;
        private bool _cover3DSceneBuilt;
        private bool _cover3DProbed;
        private bool _cover3DHookedPolicy;
        private string? _cover3DLastModeReason;
        private ImageBrush? _cover3DBrush;
        private AxisAngleRotation3D? _cover3DRotateY;
        private AxisAngleRotation3D? _cover3DRotateX;

        /// <summary>
        /// 按设置与渲染层级决定用 3D 还是 2D 封面。由 <c>ApplyOptions()</c> 调用（每次设置变更都会走一遍），
        /// 也挂在 <see cref="MotionPolicy.Changed"/> 上 —— 帧预算看门狗把层级改成 0 之后能自动退回来。
        /// </summary>
        private void ApplyCover3DMode()
        {
            MotionPolicy.ProbeRenderTier();

            if (!_cover3DHookedPolicy)
            {
                _cover3DHookedPolicy = true;
                MotionPolicy.Changed += ApplyCover3DMode;
            }

            if (!SettingsRegistry.GetBool(_options, "Cover3DEnabled", false))
            {
                SetCover3DActive(false, "关闭（使用 2D 封面）");
                return;
            }

            if (MotionPolicy.RenderTier < 2)
            {
                SetCover3DActive(false,
                    $"已拒绝：渲染层级=Tier {MotionPolicy.RenderTier}（分层窗口 + DWM backdrop 下 3D 会退化为软件渲染，需要 Tier 2；选项保持开启，换台机器即生效）");
                return;
            }

            if (!EnsureCover3DScene())
            {
                SetCover3DActive(false, "已拒绝：卡片封面槽里找不到 CardCover3D（XAML 被改过？）");
                return;
            }

            SetCover3DActive(true, $"开启（渲染层级=Tier {MotionPolicy.RenderTier}）");
        }

        private void SetCover3DActive(bool active, string reason)
        {
            _cover3DActive = active;

            if (CardCover3D != null) CardCover3D.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (CardCover2D != null) CardCover2D.Visibility = active ? Visibility.Collapsed : Visibility.Visible;

            if (!active && _cover3DRotateY != null)
            {
                // 退回 2D 时把角度归零：下次再开起来是正对镜头，不会停在侧对的位置。
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_cover3DRotateY,AxisAngleRotation3D.AngleProperty, null);
                _cover3DRotateY.Angle = 0;
            }

            // 同一个理由只记一次：ApplyOptions 启动阶段会被调用多次，不然日志里全是重复行。
            if (reason != _cover3DLastModeReason)
            {
                _cover3DLastModeReason = reason;
                IslandLog.Debug("Cover3D", "3D 封面 = " + reason);
            }
        }

        /// <summary>场景用代码搭（而不是写进 XAML）：相机/光照/网格参数要反复调，代码里改更省事。</summary>
        private bool EnsureCover3DScene()
        {
            if (_cover3DSceneBuilt) return true;
            if (CardCover3D == null) return false;

            _cover3DBrush = new ImageBrush { Stretch = Stretch.UniformToFill };

            var mesh = CoverMesh.RoundedSquare(Cover3DHalfSize, Cover3DHalfSize * (14d / 32));

            _cover3DRotateY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            _cover3DRotateX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(_cover3DRotateY));   // 换曲翻转
            transform.Children.Add(new RotateTransform3D(_cover3DRotateX));   // 立体倾斜（预留）

            var model = new GeometryModel3D(mesh, new DiffuseMaterial(_cover3DBrush)) { Transform = transform };

            var visual = new ModelVisual3D { Content = model };

            // DiffuseMaterial 只吃光：没有光就是纯黑，所以环境光给满（封面保持原色），
            // 再叠一盏弱方向光，让以后用 RotateX 做立体倾斜时有明暗层次。
            visual.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
            visual.Children.Add(new ModelVisual3D
            {
                Content = new DirectionalLight(Color.FromRgb(0xCC, 0xCC, 0xCC), new Vector3D(-0.35, 0.5, -1)),
            });

            CardCover3D.Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0, Cover3DCameraZ),
                LookDirection = new Vector3D(0, 0, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 45,
            };
            CardCover3D.Children.Add(visual);

            _cover3DSceneBuilt = true;
            IslandLog.Debug("Cover3D", "3D 场景已建立（圆角网格 + 环境光 + 方向光，相机 z=" + Cover3DCameraZ.ToString("F2", CultureInfo.InvariantCulture) + "，FOV 45）");
            return true;
        }

        /// <summary>
        /// 封面变化时同步进 3D 场景并按需播一次翻转。由 <c>ApplyArtwork()</c> 调用 ——
        /// 传进来的就是卡片/胶囊封面用的同一张图（见 <c>ApplyMedia</c>），所以 2D 与 3D 永远同源。
        /// </summary>
        private void SyncCover3DArt(BitmapSource? art)
        {
            if (_cover3DBrush != null) _cover3DBrush.ImageSource = art;

            if (!_cover3DActive || _cover3DRotateY == null) return;

            if (art == null)
            {
                // 没有封面时 3D 卡片是透明的，把角度复位就行（不播翻转）。
                AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_cover3DRotateY,AxisAngleRotation3D.AngleProperty, null);
                _cover3DRotateY.Angle = 0;
                return;
            }

            // 探针必须在"卡片正对镜头"时跑：翻转过程中卡片侧对镜头，探针必然数不到像素（假阴性）。
            if (!_cover3DProbed)
            {
                bool? ok = ProbeCover3DRendering();
                if (ok == false)
                {
                    SetCover3DActive(false, "已退回 2D：3D 渲染探针拿不到像素（本窗口里 3D 画不出来）");
                    return;
                }
                if (ok == true) _cover3DProbed = true;
            }

            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(_cover3DRotateY,AxisAngleRotation3D.AngleProperty, null);

            if (MotionPolicy.ReduceMotion)
            {
                // 减弱动效：封面直接换到位，不做翻转。
                _cover3DRotateY.Angle = 0;
                return;
            }

            var flip = new DoubleAnimation(-90, 0, TimeSpan.FromMilliseconds(Cover3DFlipMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            AnimationReset.Once(_cover3DRotateY, AxisAngleRotation3D.AngleProperty, flip, 0d);
            IslandLog.Debug("Cover3D", $"封面翻转：-90° → 0°（{Cover3DFlipMs:F0}ms，渲染层级=Tier {MotionPolicy.RenderTier}）");
        }

        /// <summary>
        /// 3D 渲染探针：把卡片封面槽渲到一张 64x64 的 <see cref="RenderTargetBitmap"/>，
        /// 数 alpha &gt; 8 的像素。返回 <c>true</c>=画出来了、<c>false</c>=一像素都没有、
        /// <c>null</c>=现在量不了（还没布局 / 还没有封面图）→ 下一次封面变化时再量。
        /// </summary>
        private bool? ProbeCover3DRendering()
        {
            if (CardCover3D == null || _cover3DBrush?.ImageSource == null) return null;
            if (!CardCover3D.IsVisible || CardCover3D.ActualWidth < 1 || CardCover3D.ActualHeight < 1) return null;

            try
            {
                int w = 64, h = 64;
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(CardCover3D);

                var buffer = new byte[w * h * 4];
                rtb.CopyPixels(buffer, w * 4, 0);

                int lit = 0;
                for (int i = 3; i < buffer.Length; i += 4)
                {
                    if (buffer[i] > 8) lit++;
                }

                IslandLog.Debug("Cover3D", $"渲染探针：非透明像素 = {lit} / {w * h}（分层窗口里 3D 到底画不画得出来）");

                if (lit == 0)
                {
                    IslandLog.Warn("Cover3D", "3D 渲染探针拿到 0 个非透明像素 → 本窗口里 3D 不可用，本次会话退回 2D 封面");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Cover3D", "3D 渲染探针失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 调试钩子 <c>ALPHA_ISLAND_COVER3D_DEMO=1</c> 用：造一张合成封面，走真实链路
        /// （<c>CardCoverBrush</c> / <c>PillCoverBrush</c> / <c>ApplyArtwork</c>），
        /// 连注两次就能在不放音乐的情况下看到两次翻转。
        /// </summary>
        internal void InjectCover3DDemo(int index)
        {
            Color a, b;
            if (index % 2 == 0)
            {
                a = Color.FromRgb(0x22, 0xD3, 0xEE);
                b = Color.FromRgb(0x8A, 0x5C, 0xFF);
            }
            else
            {
                a = Color.FromRgb(0xFF, 0xB9, 0x00);
                b = Color.FromRgb(0xFF, 0x5A, 0x5F);
            }

            var art = CreateDemoCover(a, b, "3D " + (index + 1).ToString(CultureInfo.InvariantCulture));
            IslandLog.Debug("Cover3D", $"调试钩子：注入合成封面 #{index + 1}（{art.PixelWidth}x{art.PixelHeight}）");

            CardCoverBrush.ImageSource = art;
            PillCoverBrush.ImageSource = art;
            ApplyArtwork(art);
        }

        private static BitmapSource CreateDemoCover(Color a, Color b, string caption)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, 256, 256));
                var text = new FormattedText(caption, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 84, Brushes.White, 96);
                dc.DrawText(text, new Point((256 - text.Width) / 2, (256 - text.Height) / 2));
            }

            var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }
}
