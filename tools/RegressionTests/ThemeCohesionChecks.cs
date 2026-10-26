using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

internal static partial class Program
{
    private static void ThemeCohesionChecks(string testRoot)
    {
        const string previews = "bin/ThemeCohesion156/previews/";
        var store = new ChatSessionStore(Path.Combine(testRoot, "theme-history"));
        string session = store.Create();
        store.Save(session, new[] {
            new ChatMessage("user", "查看已连接设备的电量，并提醒我在半小时后休息。"),
            new ChatMessage("assistant", "已读取设备状态，并设置休息提醒。\n你可以在电池卡片查看各设备的最新电量。") });
        store.Rename(session, "设备与休息提醒");
        string previous = store.Create();
        store.Save(previous, new[] { new ChatMessage("user", "整理今天的工作安排") });
        store.Rename(previous, "今日安排");
        var history = new ChatHistoryWindow(store, session, () => true);
        try
        {
            using var source = ThemeWindowSource(history, 860, 600, out var host);
            var sessions = (ListBox)history.FindName("SessionList");
            var messages = (ListBox)history.FindName("MessageList");
            var row = (ListBoxItem)sessions.ItemContainerGenerator.ContainerFromIndex(sessions.SelectedIndex);
            var surface = (Border)row.Template.FindName("Surface", row);
            Check(((SolidColorBrush)surface.Background).Color == ((SolidColorBrush)history.FindResource("ControlAccentSoftBrush")).Color
                && !ReferenceEquals(row.Template, new ListBoxItem().Template), "历史列表在非活动窗口中仍使用主题选中高亮");
            Check(history.FontFamily.Source.Contains("Microsoft YaHei")
                && ((SolidColorBrush)history.Background).Color == ((SolidColorBrush)history.FindResource("W11WindowBrush")).Color,
                "对话历史继承主题颜色与中英文字体回退");
            messages.SelectedIndex = 1;
            host.UpdateLayout();
            var editor = (TextBox)history.FindName("MessageEditor");
            Check(editor.Text.StartsWith("已读取设备状态") && editor.AcceptsReturn && editor.VerticalContentAlignment == VerticalAlignment.Top,
                "历史消息从编辑框顶部显示多行全文");
            editor.Text = "已读取设备状态，并设置休息提醒。\n稍后可以继续这个会话。";
            ((Button)history.FindName("SaveMessageButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(store.Load(session)[1].Content == editor.Text, "历史窗口的保存操作仍然写入本地会话");
            host.UpdateLayout();
            SaveThemePreview(host, previews + "history.png");
            store.Save(session, Enumerable.Range(0, 200).Select(i => new ChatMessage(i % 2 == 0 ? "user" : "assistant", "长历史消息 " + i)).ToArray());
            history.Reload(session); host.UpdateLayout(); PumpAnimations(20); host.UpdateLayout();
            Check(Descendants<ListBoxItem>(messages).Count() is > 0 and < 200,
                "长历史列表只生成可见范围内的容器，保留虚拟化");
            messages.SelectedIndex = 199; messages.ScrollIntoView(messages.SelectedItem);
            host.UpdateLayout(); PumpAnimations(30); host.UpdateLayout();
            var scroll = (ScrollViewer)messages.Template.FindName("PART_ScrollViewer", messages);
            Check(scroll.VerticalOffset > 0 && messages.ItemContainerGenerator.ContainerFromIndex(199) is ListBoxItem
                && editor.Text == "长历史消息 199", "长历史可以滚动到底并查看最后一条消息");
        }
        finally { history.Close(); }

        LauncherEntry? launched = null;
        var model = new LauncherViewModel(entry => { launched = entry; return Task.CompletedTask; });
        model.SetEntries(new[] {
            new LauncherEntry("battery", "电池", "查看已连接设备与充电状态", LauncherKind.Component, "battery"),
            new LauncherEntry("tools", "屏幕工具", "识字并复制、颜色吸管、翻译与字幕", LauncherKind.Component, "screen-tools"),
            new LauncherEntry("chat", "AI 对话", "向助手提问或发出指令", LauncherKind.Component, "ai-chat") });
        var launcher = new QuickLauncherWindow(model);
        try
        {
            using var source = ThemeWindowSource(launcher, 520, 440, out var host);
            host.DataContext = model;
            host.UpdateLayout(); PumpAnimations(30); host.UpdateLayout();
            var search = (TextBox)launcher.FindName("SearchBox");
            var placeholder = (TextBlock)launcher.FindName("SearchPlaceholder");
            var results = (ListBox)launcher.FindName("ResultList");
            Check(placeholder.IsVisible && !placeholder.IsHitTestVisible, "启动器空查询显示引导，提示文字不会拦截输入");
            SaveThemePreview(host, previews + "launcher.png");
            search.Text = "屏幕"; PumpAnimations(20); host.UpdateLayout();
            Check(!placeholder.IsVisible && model.Query == "屏幕" && results.Items.Count == 2, "输入后占位提示消失，搜索和 AI 指令候选正常更新");
            search.Text = ""; PumpAnimations(20); host.UpdateLayout();
            results.SelectedIndex = 1;
            Check(model.Selected?.Id == "tools", "启动器主题列表保留选中项的双向绑定");
            launcher.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(launched?.Id == "tools", "回车仍然执行当前选中的启动项");
            var selected = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(1);
            var surface = (Border)selected.Template.FindName("Surface", selected);
            Check(((SolidColorBrush)surface.Background).Color == ((SolidColorBrush)launcher.FindResource("ControlAccentSoftBrush")).Color,
                "启动器选中项使用统一主题高亮");
            model.Status = "打开失败：该应用暂时不可用，请检查路径后重试。";
            host.UpdateLayout();
            Check(Descendants<TextBlock>(host).Any(t => t.Text == model.Status && t.IsVisible), "启动失败原因在新布局中仍然可见");
        }
        finally { launcher.Close(); }

        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false) { ForceClose = true };
        try
        {
            settings.LoadFrom(new IslandOptions());
            using var source = ThemeWindowSource(settings, 880, 600, out var host);
            PrivateCall(settings, "SelectPageByTag", "about");
            host.UpdateLayout();
            var version = (TextBlock)settings.FindName("AboutVersionText");
            Check(version.IsVisible && version.Text == "版本 " + typeof(alpha_island.App).Assembly.GetName().Version!.ToString(3),
                "关于页品牌区显示实际程序集版本");
            SaveThemePreview(host, previews + "settings-about.png");
            settings.ReportSettingsSaved(true); host.UpdateLayout();
            SaveThemePreview(host, "bin/SettingsSaved158/previews/saved.png");
            settings.ReportSettingsSaved(false); host.UpdateLayout();
            SaveThemePreview(host, "bin/SettingsSaved158/previews/failed.png");
            PrivateCall(settings, "MarkSettingsPendingSave"); host.UpdateLayout();
            SaveThemePreview(host, "bin/SettingsSaved158/previews/pending.png");
            var value = new TextBlock { Style = (Style)settings.FindResource("W11Value"), Text = "1024.0 ms", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            var panel = new StackPanel { Children = { value } };
            host.Child = panel; host.UpdateLayout();
            Check(double.IsNaN(value.Width) && value.ActualWidth > 46 && value.MinWidth == 46,
                "长数值按内容扩宽，短数值保留最小对齐宽度");
            ThemeTransitionChecks(panel);
        }
        finally { settings.Close(); }
    }

    private static void ThemeTransitionChecks(StackPanel panel)
    {
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var first = new Border { Width = 100, Height = 32 };
        var second = new Border { Width = 100, Height = 32 };
        var original = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)); original.Freeze();
        ControlBrushTransition.SetBackground(first, original);
        ControlBrushTransition.SetBackground(second, original);
        panel.Children.Add(first); panel.Children.Add(second); panel.UpdateLayout(); PumpAnimations(20);
        var originalColor = original.Color;
        var hover = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)); hover.Freeze();
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            ControlBrushTransition.SetBackground(first, hover);
            var own = (SolidColorBrush)first.Background;
            Check(!ReferenceEquals(own, original) && !ReferenceEquals(own, second.Background)
                && original.Color == originalColor && ((SolidColorBrush)second.Background).Color == originalColor,
                "交互动效使用独立笔刷，不改变共享主题和其他控件");
            Check(own.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "交互过渡遵守系统动画开关");
            PumpAnimations(170);
            Check(!own.HasAnimatedProperties && own.Color == hover.Color, "100ms 过渡结束后清理时钟并保持目标颜色");
            ControlBrushTransition.SetBackground(first, original);
            MotionPolicy.Apply(true, false);
            Check(!own.HasAnimatedProperties && own.Color == originalColor, "启用削减动效会立即结束正在播放的过渡");
            ControlBrushTransition.SetBackground(first, hover);
            Check(!own.HasAnimatedProperties && own.Color == hover.Color, "削减动效下交互直接到达最终颜色");
            MotionPolicy.Apply(false, true); ControlBrushTransition.SetBackground(first, original);
            Check(!own.HasAnimatedProperties, "节能模式不创建控件交互时钟");
            MotionPolicy.Apply(false, false); ControlBrushTransition.SetBackground(first, hover);
            panel.Children.Remove(first); PumpAnimations(30);
            Check(!own.HasAnimatedProperties && own.Color == hover.Color, "卸载控件立即清理尚未完成的过渡");
        }
        finally { MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
    }

    private static HwndSource ThemeWindowSource(Window window, int width, int height, out Border host)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        host = new Border {
            Width = width, Height = height, Background = window.Background, Child = content,
            Resources = window.Resources, DataContext = window.DataContext };
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetForeground(host, window.Foreground);
        var source = new HwndSource(new HwndSourceParameters("Alpha Island theme cohesion") {
            Width = width, Height = height, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = host;
        host.UpdateLayout(); PumpAnimations(30); host.UpdateLayout();
        return source;
    }
}
