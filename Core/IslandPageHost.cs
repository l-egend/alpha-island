using System;
using System.Collections.Generic;
using System.Linq;

namespace alpha_island.Core
{
    /// <summary>
    /// 卡片分页宿主：管「有哪些页、当前第几页、每页的生命周期」。
    ///
    /// 刻意不做动画 —— 翻页动画要和凝胶弹簧抢同一批渲染帧，留在 MainWindow.Cards.cs 里
    /// （那里才能拿到 CardViewport 实测高度、_cardAnimating 门闩、ResetCardDrawers）。
    /// 这里只提供唯一真源，让"加第 4 页"变成一次 <see cref="Register"/>。
    /// </summary>
    public sealed class IslandPageHost
    {
        private sealed class Entry
        {
            public required IIslandPage Page { get; init; }
            public bool Available { get; set; } = true;
        }

        private readonly List<Entry> _entries = new();
        private IIslandPage[] _pages = Array.Empty<IIslandPage>();
        private int _activeIndex = -1;

        /// <summary>可用页集合/顺序发生变化（一般是设置改变导致某页上线或下线）。</summary>
        public event Action? PagesChanged;

        /// <summary>当前页真的换了。</summary>
        public event Action? ActiveChanged;

        /// <summary>注册一页（同 Id 覆盖注册，供设置变更后重建）。</summary>
        public void Register(IIslandPage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            int existing = _entries.FindIndex(e => e.Page.Id == page.Id);
            var entry = new Entry { Page = page };

            if (existing >= 0) _entries[existing] = entry;
            else _entries.Add(entry);

            Rebuild();
        }

        /// <summary>按当前设置重算每页是否可用，并把当前页修正回原来那一页（若它还在）。</summary>
        public void Sync(IslandOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            foreach (var entry in _entries) entry.Available = entry.Page.IsAvailable(options);
            Rebuild();
        }

        public bool Unregister(string id)
        {
            int removed = _entries.RemoveAll(entry => entry.Page.Id == id);
            if (removed == 0) return false;
            Rebuild();
            return true;
        }

        /// <summary>冻结后的可用页顺序。</summary>
        public IReadOnlyList<IIslandPage> Pages => _pages;

        public int Count => _pages.Length;

        /// <summary>当前页下标；-1 表示还没有可用页。</summary>
        public int ActiveIndex => _activeIndex;

        public IIslandPage? ActivePage =>
            _activeIndex >= 0 && _activeIndex < _pages.Length ? _pages[_activeIndex] : null;

        public string ActiveId => ActivePage?.Id ?? string.Empty;

        public int IndexOf(string id)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                if (string.Equals(_pages[i].Id, id, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>找不到（该页在当前设置下不可用）时退到第一页，绝不给调用方 -1。</summary>
        public int IndexOfOrDefault(string id) => Math.Max(0, IndexOf(id));

        /// <summary>
        /// 切到指定页。前页 <see cref="IIslandPage.OnDeactivated"/>、目标页
        /// <see cref="IIslandPage.OnActivated"/>（重切同一页也会再调一次 OnActivated，复刻老行为）。
        /// 返回 true 表示真的换了页。
        /// </summary>
        public bool SetActiveIndex(int index)
        {
            if (index < 0 || index >= _pages.Length) return false;

            var previous = ActivePage;
            bool changed = index != _activeIndex;

            if (changed) previous?.OnDeactivated();

            _activeIndex = index;
            ActivePage?.OnActivated();

            if (changed) ActiveChanged?.Invoke();
            return changed;
        }

        /// <summary>音量卡抽屉撑高/复位：把新视口高度广播给每一页。</summary>
        public void BroadcastViewportHeight(double height)
        {
            foreach (var page in _pages) page.OnViewportHeightChanged(height);
        }

        /// <summary>
        /// 重算冻结页列表。OrderBy 是稳定排序，同 Order 时保持注册顺序。
        /// </summary>
        private void Rebuild()
        {
            var previous = ActivePage;
            var before = _pages;
            var list = new List<IIslandPage>(_entries.Count);
            foreach (var entry in _entries.OrderBy(e => e.Page.Order))
            {
                if (entry.Available) list.Add(entry.Page);
            }

            _pages = list.ToArray();
            if (previous != null)
            {
                int preserved = IndexOf(previous.Id);
                _activeIndex = preserved >= 0 ? preserved : Math.Min(_activeIndex, _pages.Length - 1);
            }
            if (!ReferenceEquals(previous, ActivePage))
            {
                previous?.OnDeactivated();
                ActivePage?.OnActivated();
                ActiveChanged?.Invoke();
            }
            if (!before.SequenceEqual(_pages)) PagesChanged?.Invoke();
        }
    }
}
