using System;
using System.Windows;

namespace alpha_island.Core
{
    /// <summary>
    /// 内置卡片页的固定 Id。业务代码一律按 Id 判断当前页，**不要按序号** ——
    /// 序号会随「新增页 / 某页在当前设置下不可用」而变。
    /// </summary>
    public static class IslandPageIds
    {
        public const string Music = "music";
        public const string Clipboard = "clipboard";
        public const string Volume = "volume";
        public const string Ai = "ai";
        public const string NetSpeed = "netspeed";
        public const string System = "system";
        public const string Notifications = "notifications";

        /// <summary>下载进度页（第二批③）：浏览器（Chrome / Edge）与 Steam 的下载进度。</summary>
        public const string Download = "download";

    }

    /// <summary>
    /// 二级展开里的一屏卡片页。老的实现把「只有 3 页」写死在各处
    /// （CardGeometry 的高度三元判断、SwitchCardPage 里 p0/p1/p2 成对硬写、三个翻页点各一个处理器），
    /// 于是"加第 4 页"要改五处且很容易漏 —— 这张接口就是那一页的完整契约。
    /// </summary>
    public interface IIslandPage
    {
        /// <summary>稳定 Id，见 <see cref="IslandPageIds"/>。</summary>
        string Id { get; }

        /// <summary>组件菜单与诊断里的显示名。</summary>
        string Title { get; }

        /// <summary>排序权重（同权重按注册顺序）。</summary>
        int Order { get; }

        /// <summary>在当前设置下这一页是否可用（不可用的页不参与翻页，也不出现在组件菜单中）。</summary>
        bool IsAvailable(IslandOptions options);

        /// <summary>这一页想要的卡片高度（DIP，不含音量卡抽屉撑高）。宿主取它与「歌词高度」的大者。</summary>
        double PreferredHeight { get; }

        /// <summary>页面内容层（XAML 里那个带 TranslateTransform 的具名 Grid）。</summary>
        UIElement View { get; }

        /// <summary>成为当前页（含"重切同一页"，老实现每次都刷新内容）。</summary>
        void OnActivated();

        /// <summary>不再是当前页。</summary>
        void OnDeactivated();

        /// <summary>
        /// 卡片视口高度变化时回调（音量卡抽屉撑高 / 复位）。
        /// 视口是宿主共用的，页要自己决定这层的高度跟不跟着长 —— 不跟就会被 ClipToBounds 裁掉。
        /// </summary>
        void OnViewportHeightChanged(double height);
    }

    /// <summary>用委托凑一张页，避免为每张页单独写一个类。</summary>
    public sealed class DelegateIslandPage : IIslandPage
    {
        private readonly Func<IslandOptions, bool> _available;
        private readonly Action? _activated;
        private readonly Action? _deactivated;
        private readonly Action<double>? _viewportChanged;

        public DelegateIslandPage(
            string id,
            string title,
            UIElement view,
            int order,
            double preferredHeight,
            Func<IslandOptions, bool>? available = null,
            Action? activated = null,
            Action? deactivated = null,
            Action<double>? viewportChanged = null)
        {
            Id = id;
            Title = title;
            View = view;
            Order = order;
            PreferredHeight = preferredHeight;
            _available = available ?? (_ => true);
            _activated = activated;
            _deactivated = deactivated;
            _viewportChanged = viewportChanged;
        }

        public string Id { get; }
        public string Title { get; }
        public UIElement View { get; }
        public int Order { get; }
        public double PreferredHeight { get; }

        public bool IsAvailable(IslandOptions options) => _available(options);
        public void OnActivated() => _activated?.Invoke();
        public void OnDeactivated() => _deactivated?.Invoke();
        public void OnViewportHeightChanged(double height) => _viewportChanged?.Invoke(height);
    }

}
