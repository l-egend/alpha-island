using alpha_island.Core;
using alpha_island.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private static void FileTransferExportChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        PrivateCall(window, "StartFileTransferService");
        var transfer = AgentField<FileTransferService>(window, "_fileTransfer");
        string path = Path.Combine(testRoot, "复制和剪切拖出.txt");
        File.WriteAllText(path, "owned export regression fixture");
        try
        {
            FileTransferStagingChecks(window, root, transfer, path);
            foreach (bool isCut in new[] { false, true })
            foreach (bool clipboardLocked in new[] { false, true })
            {
                transfer.Clear();
                transfer.AddFiles(new[] { path }, isCut);
                PrivateCall(window, "SetState", IslandState.Tray);
                SetAgentField(window, "_clipboardLocked", clipboardLocked);
                PumpAnimations(80); root.UpdateLayout();
                var row = TransferRow(window.TransferItemsList);
                Check(row != null && row.IsVisible, "真实文件条目模板已显示");
                var icon = VisualChild<System.Windows.Controls.Image>(row!);
                Check(icon != null, "从文件图标子元素发起拖动");
                icon!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Check(ReferenceEquals(AgentField<TransferItem?>(window, "_dragSourceItem"), transfer.Items.Single()),
                    "文件按下经过父岛和条目的真实路由，记录待拖出项");
                Check(!AgentField<bool>(window, "_isSwiping") && !window.IslandBody.IsMouseCaptured,
                    $"{(isCut ? "剪切" : "复制")}文件拖出不被收起手势或剪切板鼠标捕获抢走（剪切板驻留={clipboardLocked}）");
                var start = AgentField<System.Windows.Point>(window, "_swipeStartScreenPoint");
                PrivateCall(window, "OnMouseHookDragMove", null,
                    ((int)start.X + 20, (int)start.Y - 30, (int)start.X, (int)start.Y));
                PumpAnimations(50);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Tray,
                    "向上拖出文件保持中转站，不被全局收起手势提前折叠");
                icon.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
                Check(transfer.Count == 1 && File.Exists(path), "尚未完成目标投放时保留条目和源文件");
                PrivateCall(window, "OnMouseHookDragEnd", null, EventArgs.Empty);
                SetAgentField(window, "_dragSourceItem", null);
            }
            window.IslandBody.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(AgentField<bool>(window, "_isSwiping"), "岛体空白区域仍支持原有收起手势");
            window.IslandBody.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Check(!AgentField<bool>(window, "_isSwiping") && !window.IslandBody.IsMouseCaptured,
                "普通收起手势松手后释放鼠标状态");
        }
        finally
        {
            Mouse.Capture(null);
            SetAgentField(window, "_clipboardLocked", false);
            SetAgentField(window, "_dragSourceItem", null);
            PrivateCall(window, "OnMouseHookDragEnd", null, EventArgs.Empty);
            transfer.Clear();
        }
    }

    private static void FileTransferStagingChecks(alpha_island.MainWindow window, Grid root,
        FileTransferService transfer, string path)
    {
        System.Windows.DragEventArgs DragArgs(System.Windows.IDataObject data,
            System.Windows.DragDropEffects effects = System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move)
        {
            var constructor = typeof(System.Windows.DragEventArgs).GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            var args = (System.Windows.DragEventArgs)constructor.Invoke(new object[] { data,
                System.Windows.DragDropKeyStates.LeftMouseButton, effects, window.IslandBody, new System.Windows.Point(5, 5) });
            args.RoutedEvent = System.Windows.DragDrop.PreviewDragEnterEvent;
            return args;
        }
        var payload = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { path });
        var text = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, path);
        PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.System); root.UpdateLayout();
        var rejected = DragArgs(text); PrivateCall(window, "Window_DragEnter", window, rejected);
        Check(rejected.Effects == System.Windows.DragDropEffects.None && transfer.Count == 0,
            "文件路径文本不会被当作可拖出的真实文件");
        var enter = DragArgs(payload); PrivateCall(window, "Window_DragEnter", window, enter);
        Check(AgentField<IslandState>(window, "_state") == IslandState.Tray && transfer.Count == 0,
            "真实文件进入中转站时展开，松手前不提前暂存");
        PrivateCall(window, "Window_DragLeave", window, enter); PumpAnimations(220);
        Check(AgentField<IslandState>(window, "_state") == IslandState.Card && transfer.Count == 0,
            "拖入途中取消恢复原卡片且不残留文件项");
        foreach (bool cut in new[] { false, true })
        {
            var drop = DragArgs(payload);
            PrivateCall(window, cut ? "DropZoneCut_Drop" : "DropZoneCopy_Drop",
                cut ? window.DropZoneCut : window.DropZoneCopy, drop);
            Check(drop.Effects == System.Windows.DragDropEffects.Copy && transfer.Count == 1
                && transfer.Items.Single().IsCut == cut && File.Exists(path),
                $"{(cut ? "剪切" : "复制")}分区记录后续拖出意图，源文件在暂存时保持完整且条目去重");
        }
        SetAgentField(window, "_exportingTransferItem", true);
        var self = DragArgs(payload); PrivateCall(window, "Window_DragEnter", window, self);
        Check(self.Effects == System.Windows.DragDropEffects.None && transfer.Count == 1,
            "拖出后落回中转站自身不误确认完成");
        SetAgentField(window, "_exportingTransferItem", false);
        transfer.Clear();
        var moveOnly = DragArgs(payload, System.Windows.DragDropEffects.Move);
        PrivateCall(window, "Window_DragEnter", window, moveOnly);
        Check(moveOnly.Effects == System.Windows.DragDropEffects.None && transfer.Count == 0 && File.Exists(path),
            "暂存不会向只允许立即移动的来源误确认剪切");
    }

    private static Border? TransferRow(DependencyObject element)
    {
        if (element is Border border && border.DataContext is TransferItem) return border;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (TransferRow(VisualTreeHelper.GetChild(element, i)) is { } row) return row;
        return null;
    }

    private static T? VisualChild<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (VisualChild<T>(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }
}
