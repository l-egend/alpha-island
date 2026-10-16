using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void LiquidGlassCoreChecks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var shape = new LiquidIslandShape();
                var pill = new IslandGeometry(344, 48, 12, new CornerRadius(24));
                shape.Update(pill);
                Check(shape.Geometry.Figures[0].Segments.OfType<ArcSegment>().Count() == 4, "液态轮廓保留四角 ArcSegment");
                Check(shape.Geometry.FillContains(new Point(172, 1)), "静止胶囊顶部保持平滑，无永久凹口");
                shape.Update(pill, 1, 0);
                Check(!shape.Geometry.FillContains(new Point(172, 1)), "拉伸时轮廓中部真实收颈，不是等比缩放");
                Check(shape.Geometry.FillContains(new Point(172, 24)), "收颈保留可见实体，不断裂");
                Check(shape.Geometry.Bounds.Left >= -.01 && shape.Geometry.Bounds.Right <= 344.01, "路径不越过岛体包围盒");
                for (int i = 0; i <= 50; i++)
                {
                    double p = i / 50d;
                    shape.Update(new IslandGeometry(130 + 300 * p, 10 + 290 * p, 0, new CornerRadius(28 * p)), LiquidIslandShape.Pulse(p), LiquidIslandShape.Pulse(p));
                    Check(double.IsFinite(shape.Geometry.GetArea()) && shape.Geometry.GetArea() > 0, "液态路径全程有限且有面积 " + i);
                }
                Check(!shape.IsDeformed && LiquidIslandShape.Pulse(0) == 0 && LiquidIslandShape.Pulse(1) == 0, "动画首尾精确归位");
                var left = GlassLighting.Estimate(new[] { new GlassLightSample(new(-1, 0), Colors.White), new GlassLightSample(new(1, 0), Colors.Black) });
                var right = GlassLighting.Estimate(new[] { new GlassLightSample(new(-1, 0), Colors.Black), new GlassLightSample(new(1, 0), Colors.White) });
                Check(left.Direction.X < -.99 && right.Direction.X > .99, "桌面明暗改变会翻转高光方向");
                var uniform = GlassLighting.Estimate(new[] { new GlassLightSample(new(0, 1), Colors.Black), new GlassLightSample(new(1, 0), Colors.Black) });
                Check(uniform.Direction.LengthSquared > .9 && uniform.Contrast == 0, "暗场和均匀场不会产生随机高光");
                var blended = GlassLight.Blend(left, right, .5);
                Check(double.IsFinite(blended.Direction.X) && blended.Direction.LengthSquared > .99, "相反光源平滑切换不产生 NaN");
                var body = new Rect(300, 100, 344, 48); var desktop = new Rect(0, 0, 1920, 1080);
                var area = DesktopLightSampler.ProbeArea(body, new(-1, 0), desktop, new[] { body, new Rect(235, 100, 55, 48) });
                Check(area.HasValue && !area.Value.IntersectsWith(body) && !area.Value.IntersectsWith(new Rect(235, 100, 55, 48)), "背景取色避开主岛与旁边的小岛");
                Check(DesktopLightSampler.ProbeArea(new Rect(10, 0, 130, 10), new(0, -1), desktop, Array.Empty<Rect>()) == null, "屏幕顶部不读取屏外像素");
                var bridge = LiquidCellMotion.Evaluate(new Rect(20, 20, 36, 36), new Rect(64, 14, 344, 48), .5, LiquidMotionKind.Split).Neck;
                Check(bridge is not null && bridge.FillContains(new Point(60, 38)), "多岛分离时有真实的连接颈");
                Check(LiquidCellMotion.Evaluate(new Rect(20, 20, 36, 36), new Rect(64, 14, 344, 48), 1, LiquidMotionKind.Split).Neck == null, "分离结束后清除连接，不留下细线");
                using var backdrop = BackdropWindow.TryCreate();
                Check(backdrop is { IsAvailable: true }, "原生 DWM 玻璃可用");
                shape.Update(pill, .8, 0);
                Check(backdrop!.SetOutline(shape.Geometry), "Direct2D 路径直接交给 Composition 裁剪: " + backdrop.Failure);
                Check(backdrop.ContourUpdates == 1, "原生矢量裁剪已真正提交");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
