using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using alpha_island.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using RadioButton = System.Windows.Controls.RadioButton;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void SettingsCategoryChecks()
    {
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "PomodoroFocusMinutes", 37);
        SettingsRegistry.SetExtra(options, "plugin/vivo.office/enabled", true);
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false);
        settings.LoadFrom(options);
        settings.OptionsChanged += (_, updated) => options.CopyFrom(updated);
        var root = (Grid)settings.Content; settings.Content = null; root.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Settings categories regression")
        { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = root;
        bool oldReduce = alpha_island.Animation.MotionPolicy.ReduceMotion;
        bool oldPower = alpha_island.Animation.MotionPolicy.PowerSave;
        try
        {
            alpha_island.Animation.MotionPolicy.Apply(true, false);
            PumpAnimations(20); root.UpdateLayout();
            var nav = (StackPanel)settings.FindName("NavPanel");
            var items = nav.Children.OfType<RadioButton>().ToArray();
            foreach (var item in items)
            {
                item.IsChecked = true; root.UpdateLayout();
                Check(items.Count(i => i.IsChecked == true) == 1
                    && Descendants<StackPanel>(root).Count(p => p.Name.StartsWith("Page_") && p.Visibility == Visibility.Visible) == 1,
                    "设置导航仅显示目标页面 " + item.Tag);
            }
            foreach (var (controlName, pageName) in new[] {
                ("PomodoroFocusMinutesBox", "Page_Focus"), ("PomodoroSoundBox", "Page_Focus"),
                ("MediaWindowHandoffToggle", "Page_Media"), ("MediaPlaybackNotifyToggle", "Page_Media"),
                ("VolumeNotifyToggle", "Page_Notifications"), ("BluetoothNotifySlider", "Page_Notifications"),
                ("NotificationQuietToggle", "Page_Notifications"), ("GreetingToggle", "Page_Notifications"),
                ("CpuMonitorToggle", "Page_System"), ("BatteryNotifyToggle", "Page_System"),
                ("ClipboardNotifyToggle", "Page_Behavior") })
            {
                var control = (DependencyObject)settings.FindName(controlName);
                DependencyObject? node = control;
                while (node != null && (node is not FrameworkElement element || !element.Name.StartsWith("Page_")))
                    node = LogicalTreeHelper.GetParent(node);
                Check(node is FrameworkElement page && page.Name == pageName, "设置控件放在正确分类 " + controlName);
            }
            var search = (TextBox)settings.FindName("SearchBox");
            foreach (var (query, page) in new[] { ("PomodoroFocusMinutes", "focus"), ("MediaWindowHandoffEnabled", "media"),
                ("NotificationBlockedApps", "notifications"), ("VolumeNotifyEnabled", "notifications"), ("CpuMonitorEnabled", "system") })
            {
                search.Text = query;
                Check(items.Single(i => Equals(i.Tag, page)).Visibility == Visibility.Visible
                    && items.Count(i => i.Visibility == Visibility.Visible) == 1, "搜索新分类准确定位 " + query);
            }
            search.Text = "";
            settings.OpenPomodoroSettings(); PumpAnimations(20); root.UpdateLayout();
            Check(((FrameworkElement)settings.FindName("Page_Focus")).Visibility == Visibility.Visible
                && ((FrameworkElement)settings.FindName("Page_System")).Visibility == Visibility.Collapsed,
                "番茄钟卡片入口直接打开独立设置页");
            var focus = (TextBox)settings.FindName("PomodoroFocusMinutesBox");
            Check(focus.Text == "37", "移动后的番茄钟设置正确回填原值");
            focus.Text = "42"; PumpAnimations(380);
            Check(SettingsRegistry.GetInt(options, "PomodoroFocusMinutes", 0) == 42
                && SettingsRegistry.GetBool(options, "plugin/vivo.office/enabled", false), "新分类编辑保存且保留插件配置");
            var useLongBreak = (CheckBox)settings.FindName("PomodoroUseLongBreakToggle");
            useLongBreak.IsChecked = true;
            Check(((FrameworkElement)settings.FindName("PomodoroLongBreakMinutesBox")).IsEnabled,
                "移动后长休息依赖仍可启用");
            Directory.CreateDirectory("bin/settings-category-previews");
            foreach (string tag in new[] { "focus", "media", "notifications", "system", "behavior" })
            {
                PrivateCall(settings, "SelectPageByTag", tag); root.UpdateLayout();
                SaveThemePreview(root, "bin/settings-category-previews/" + tag + ".png");
            }
        }
        finally
        {
            alpha_island.Animation.MotionPolicy.Apply(oldReduce, oldPower);
            settings.ForceClose = true; settings.Close();
        }
    }
}
