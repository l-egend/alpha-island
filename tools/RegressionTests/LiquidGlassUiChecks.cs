using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void LiquidGlassUiChecks(alpha_island.MainWindow window, Grid root)
    {
        int tier = MotionPolicy.RenderTier;
        bool reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave;
        var options = AgentField<IslandOptions>(window, "_options");
        bool gel = options.GelAnimation;
        var animator = AgentField<IslandAnimator>(window, "_animator");
        var contour = AgentField<LiquidIslandShape>(window, "_liquidShape");
        var tick = typeof(IslandAnimator).GetField("_lastTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var render = typeof(IslandAnimator).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Advance(int frames)
        {
            for (int i = 0; i < frames && animator.IsRunning; i++)
            {
                tick.SetValue(animator, (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - 1d / 60);
                render.Invoke(animator, new object?[] { null, EventArgs.Empty }); root.UpdateLayout();
            }
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false); options.GelAnimation = true;
            animator.SetImmediate(new(130, 10, 0, new CornerRadius(0, 0, 10, 10)));
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Advance(10);
            Check(contour.IsDeformed && ReferenceEquals(window.IslandBody.Clip, contour.Geometry), "实际 Bar→Pill 驱动共享液态裁剪轮廓");
            double priorNeck = contour.NeckX + contour.NeckY;
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System);
            Check(Math.Abs(contour.NeckX + contour.NeckY - priorNeck) < .001, "中途改为 Card 不重置液态应变，不发生轮廓跳变");
            Advance(12);
            Check(contour.IsDeformed && window.IslandBody.Height > 48, "实际 Pill→Card 具有纵向收颈和高度增长");
            Advance(120);
            Check(!animator.IsRunning && !contour.IsDeformed, "卡片动画收尾清除应变并释放动画回调");
            Check(Math.Abs(contour.Geometry.Bounds.Width - window.IslandBody.Width) < .01, "静止卡片材质、描边和命中区域尺寸相同");
            PrivateCall(window, "AttachLiquidAdorners");
            var chrome = AgentField<Dictionary<Border, LiquidGlassChrome>>(window, "_glassChrome");
            Check(chrome.ContainsKey(window.IslandBody) && chrome.Values.All(item => !item.IsHitTestVisible), "玻璃高光层不接管任何鼠标点击");
            var point = window.IslandBody.TranslatePoint(new Point(window.IslandBody.ActualWidth / 2, 32), root);
            Check(root.InputHitTest(point) is not Adorner, "卡片上实际输入命中不被边缘材质遮挡");
            MotionPolicy.Apply(true, false);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Check(!animator.IsRunning && !contour.IsDeformed && window.IslandBody.Height == 48, "减弱动效立即落位并清除液态拉伸");
            MotionPolicy.Apply(false, true);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System); Advance(120);
            Check(!contour.IsDeformed && AgentField<IslandTimer?>(window, "_glassLightTimer")?.IsEnabled == false, "省电模式停止光源采样并使用简单轮廓");
            Check(!window.LiquidFrameLoopActive && window.GlassLightSamples == 0, "隔离或静止视图没有额外常驻渲染/采样循环");
            LiquidChromePixelChecks();
        }
        finally { options.GelAnimation = gel; MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, power); }
    }

    private static void LiquidChromePixelChecks()
    {
        var body = new Border { Width = 344, Height = 48, CornerRadius = new CornerRadius(24) };
        body.Measure(new Size(344, 48)); body.Arrange(new Rect(0, 0, 344, 48));
        var shape = new LiquidIslandShape(); shape.Update(new(344, 48, 0, new CornerRadius(24)));
        var chrome = new LiquidGlassChrome(body, shape);
        chrome.SetMaterial(Colors.Cyan, .08, true);
        (double Left, double Right) Render(Vector direction)
        {
            chrome.SetLight(new GlassLight(direction, 1, 1, Colors.White), false);
            chrome.Measure(new Size(344, 48)); chrome.Arrange(new Rect(0, 0, 344, 48)); chrome.UpdateLayout();
            var bitmap = new RenderTargetBitmap(344, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chrome);
            byte[] pixels = new byte[344 * 48 * 4]; bitmap.CopyPixels(pixels, 344 * 4, 0);
            Check(pixels[3] == 0 && pixels[(343 * 4) + 3] == 0, "真实高光像素不溢出圆角");
            double left = 0, right = 0;
            for (int y = 0; y < 48; y++) for (int x = 0; x < 8; x++)
            { left += pixels[(y * 344 + x) * 4 + 3]; right += pixels[(y * 344 + 343 - x) * 4 + 3]; }
            return (left, right);
        }
        var fromLeft = Render(new(-1, 0)); var fromRight = Render(new(1, 0));
        Check(fromLeft.Left > fromLeft.Right * 1.15 && fromRight.Right > fromRight.Left * 1.15, "实际像素高光跟随左右光源移动");
    }
}
