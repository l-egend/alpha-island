using System;
using System.Collections.Generic;
using System.Text;

namespace alpha_island.Core;

/// <summary>卫星挂在主岛的哪一侧。</summary>
public enum SatelliteSide
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// 多岛卫星（置顶气泡 / 左侧音乐圆球 / 卡片态旁路提示…）的横向偏移统管。
///
/// 背景：卫星不参与 WPF 布局（都在同一格子里居中 + <c>TranslateTransform</c> 手动挪），
/// 所以"互不重叠"只能由一处统一算。原来每个卫星各自硬编码一个 X（置顶气泡 226、音乐球 -204/-222），
/// 新增一个卫星就得手工改所有数字，加第三个必然撞在一起。
///
/// 规则（每个卫星只需声明"我想贴在哪"+"我多宽"）：
///   · 洋细到中心的距离 <c>inner</c> = 卫星靠近主岛的那条边到岛中心；
///   · 同侧多个卫星按注册顺序依次往外放，后面的起点不能比前面已占用的外边界更靠内：
///       placed = max(自己的 inner, 前一个的 outer + gap)
///   · 返回的偏移 = ±(placed + width / 2)，可直接写进 <c>TranslateTransform.X</c>（元素是居中锚点）。
///
/// 这个类**没有任何 WPF 引用**：几何由外面用 lambda 喂进来（<c>() =&gt; IslandBody.Width / 2</c>），
/// 所以能单测、也能在日志里 Dump 出真实数字。
/// </summary>
public sealed class SatelliteRail
{
    private sealed class Entry
    {
        public required string Key;
        public required SatelliteSide Side;
        public required Func<double> Inner;
        public required Func<double> Width;
        public Func<bool>? Visible;
        public double Gap = 8.0;
    }

    private readonly List<Entry> _entries = new();

    /// <summary>注册（或覆盖）一个卫星。<paramref name="inner"/> 是"外边缘到岛中心"的目标距离。</summary>
    public void Register(string key, SatelliteSide side, Func<double> inner, Func<double> width,
                         Func<bool>? visible = null, double gap = 8.0)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key 不能为空", nameof(key));

        _entries.RemoveAll(e => e.Key == key);
        _entries.Add(new Entry { Key = key, Side = side, Inner = inner, Width = width, Visible = visible, Gap = gap });
    }

    public void Unregister(string key) => _entries.RemoveAll(e => e.Key == key);

    /// <summary>清空指定侧的所有卫星登记（用于新一轮重排重新注册）。</summary>
    public void ClearSide(SatelliteSide side) => _entries.RemoveAll(e => e.Side == side);

    /// <summary>该卫星此刻是否可见（没写判据时恒 true）。</summary>
    public bool IsVisible(string key)
    {
        foreach (var e in _entries)
        {
            if (e.Key != key) continue;
            try { return e.Visible?.Invoke() ?? true; }
            catch { return true; }
        }
        return false;
    }

    /// <summary>
    /// 该卫星此刻应处的 X 偏移（居中锚点，左负右正）。
    /// 计算时把同侧"比它先注册且可见"的卫星当成已占用，所以新增卫星不需要回头改老卫星的数字。
    /// </summary>
    public double Offset(string key)
    {
        Entry? self = null;
        foreach (var e in _entries) if (e.Key == key) { self = e; break; }
        if (self is null) return 0.0;

        double cursor = 0.0;   // 该侧已占用的外边界（到岛中心），只在放过东西之后有意义
        bool any = false;

        foreach (var e in _entries)
        {
            if (e.Side != self.Side) continue;
            bool isSelf = ReferenceEquals(e, self);
            if (!isSelf && !SafeVisible(e)) continue;

            double inner = Math.Max(0.0, SafeNumber(e.Inner));
            double width = Math.Max(1.0, SafeNumber(e.Width));
            double placed = any ? Math.Max(inner, cursor + e.Gap) : inner;
            double x = placed + width / 2.0;

            if (isSelf) return self.Side == SatelliteSide.Left ? -x : x;

            cursor = placed + width;
            any = true;
        }

        return 0.0;
    }

    /// <summary>供日志用：把当前每个卫星的可见性与偏移打成一行（排障"两个卫星撞了"时唯一需要看的东西）。</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
        {
            if (sb.Length > 0) sb.Append("；");
            sb.Append(e.Key).Append(e.Side == SatelliteSide.Left ? " 左" : " 右")
              .Append(SafeVisible(e) ? " 可见" : " 隐藏")
              .Append(" X=").Append(Offset(e.Key).ToString("0.0"));
        }
        return sb.ToString();
    }

    private static bool SafeVisible(Entry e)
    {
        try { return e.Visible?.Invoke() ?? true; }
        catch { return true; }
    }

    private static double SafeNumber(Func<double> f)
    {
        try
        {
            double v = f();
            return double.IsFinite(v) ? v : 0.0;
        }
        catch { return 0.0; }
    }
}
