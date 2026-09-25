using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace alpha_island.Services
{
    /// <summary>中转站单条文件项。</summary>
    public sealed class TransferItem : INotifyPropertyChanged
    {
        private bool _isCut;
        private ImageSource? _iconSource;

        public string FullPath { get; }
        public string FileName { get; }

        public bool IsCut
        {
            get => _isCut;
            set
            {
                if (_isCut == value) return;
                _isCut = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCut)));
            }
        }

        /// <summary>
        /// 图标由后台线程异步补齐（见 FileTransferService.StartIconFetch），
        /// 因此必须是可变属性并通知绑定，界面才能从“无图标”更新为真实图标。
        /// </summary>
        public ImageSource? IconSource
        {
            get => _iconSource;
            set
            {
                if (ReferenceEquals(_iconSource, value)) return;
                _iconSource = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public TransferItem(string fullPath, bool isCut, ImageSource? iconSource)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(FileName)) FileName = fullPath;
            _isCut = isCut;
            _iconSource = iconSource;
        }
    }

    /// <summary>
    /// 文件中转站服务。
    /// 管理拖入中转站的暂存文件列表（支持区分复制/剪切模式），并在全部文件拖出后通知收回。
    /// </summary>
    public sealed class FileTransferService
    {
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<TransferItem> _items = new();

        // 路径 -> 条目 索引：原实现每加入一个文件就线性扫描整个列表（批量添加是 O(n²)），
        // 拖入几十个文件时会白白占用 UI 线程。索引只在 UI 线程访问。
        private readonly Dictionary<string, TransferItem> _index =
            new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<TransferItem> Items => _items;

        public int Count => _items.Count;

        /// <summary>项变动（添加/移除/清空）时触发。</summary>
        public event EventHandler? ItemsChanged;

        /// <summary>所有文件全部被拖出清空时触发。</summary>
        public event EventHandler? AllFilesEmptied;

        public FileTransferService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>添加或更新文件至中转站。</summary>
        public void AddFiles(string[] filePaths, bool isCut)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            // 这里只做路径校验、去重和建条目（微秒级）；耗时的图标提取
            // （实测平均约 7ms/文件，首个可达 120ms 以上）交给后台线程异步补齐。
            // 原实现把 GetFileIcon 放在 _dispatcher.Invoke 的循环里，拖入 N 个文件
            // 就会把 UI 线程（连同挂在 UI 线程上的全局鼠标钩子）冻结 N×7ms。
            _dispatcher.Invoke(() =>
            {
                var added = new List<TransferItem>();
                bool changed = false;

                foreach (var path in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                        continue;

                    if (_index.TryGetValue(path, out var existing))
                    {
                        // 同一路径重复拖入：只更新复制/剪切模式。
                        if (existing.IsCut != isCut)
                        {
                            existing.IsCut = isCut;
                            changed = true;
                        }
                        continue;
                    }

                    var item = new TransferItem(path, isCut, null);
                    _index[path] = item;
                    _items.Add(item);
                    added.Add(item);
                    changed = true;
                }

                if (changed) ItemsChanged?.Invoke(this, EventArgs.Empty);

                if (added.Count > 0) StartIconFetch(added);
            });
        }

        /// <summary>后台批量提取图标，逐个回到 UI 线程写入（图标绑定会自行刷新）。</summary>
        private void StartIconFetch(List<TransferItem> items)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in items)
                {
                    var icon = GetFileIcon(item.FullPath);
                    if (icon == null) continue;
                    _dispatcher.InvokeAsync(() => item.IconSource = icon);
                }
            });
        }

        /// <summary>从暂存列表中移除单项（例如被拖出至目标文件夹后）。</summary>
        public void RemoveItem(TransferItem item)
        {
            if (item == null) return;

            _dispatcher.Invoke(() =>
            {
                if (_items.Remove(item))
                {
                    _index.Remove(item.FullPath);
                    ItemsChanged?.Invoke(this, EventArgs.Empty);

                    if (_items.Count == 0)
                    {
                        AllFilesEmptied?.Invoke(this, EventArgs.Empty);
                    }
                }
            });
        }

        public void Clear()
        {
            _dispatcher.Invoke(() =>
            {
                if (_items.Count > 0)
                {
                    _items.Clear();
                    _index.Clear();
                    ItemsChanged?.Invoke(this, EventArgs.Empty);
                    AllFilesEmptied?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        private static ImageSource? GetFileIcon(string path)
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[FileTransfer] 提取图标失败: " + ex.Message);
            }
            return null;
        }
    }
}
