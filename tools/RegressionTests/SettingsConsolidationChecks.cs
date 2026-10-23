using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using alpha_island.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void SettingsConsolidationChecks(string testRoot)
    {
        var options = new IslandOptions().Normalized();
        var settings = new alpha_island.Settings.SettingsWindow(false);
        settings.LoadFrom(options); settings.OptionsChanged += (_, updated) => options.CopyFrom(updated);
        var root = (Grid)settings.Content; settings.Content = null; root.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Consolidated settings regression")
        { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = root;
        try
        {
            root.UpdateLayout();
            Check(settings.FindName("MediaPlaybackNotifyToggle") is CheckBox
                && settings.FindName("UpNextToggle") == null && settings.FindName("PauseNotifyToggle") == null
                && settings.FindName("PlayNotifyToggle") == null, "换曲、暂停、继续播放共用一个提示开关");
            var items = ((StackPanel)settings.FindName("NavPanel")).Children.OfType<RadioButton>().ToArray();
            Check(items.Any(item => Equals(item.Tag, "general")) && items.Any(item => Equals(item.Tag, "assistant")), "常规和智能助手拥有独立导航入口");
            Check(!items.Any(item => item.Tag is "audio" or "lyrics" or "island"), "外观、音频和歌词归入外观与动效");
            foreach (string name in new[] { "LiquidGlassToggle", "GelAnimationToggle", "TwoStepExpandToggle", "ShowCoverToggle", "Cover3DToggle", "KeepCompactToggle", "ClipboardAutoCollapseSecondsSlider", "PowerSaveToggle", "ReduceMotionToggle", "PreviewChargingButton", "PreviewUnpluggedButton", "PreviewChargingIslandButton", "NotificationQQToggle", "NotificationWeChatToggle", "AirplaneBarToggle", "AirplaneNotifyToggle", "NetSpeedThresholdSlider", "PowerNotifySlider" })
                Check(settings.FindName(name) == null, "移除精简设置控件 " + name);
            Check(settings.FindName("AppearanceLyricsBranch") is StackPanel && settings.FindName("AppearanceAudioBranch") is StackPanel,
                "音频和歌词分组直接展示，不使用折叠选项");
            var playback = (CheckBox)settings.FindName("MediaPlaybackNotifyToggle");
            playback.IsChecked = false;
            Check(!options.UpNextEnabled && !options.PauseNotifyEnabled && !options.PlayNotifyEnabled, "关闭统一开关立即停用三类播放提示");
            playback.IsChecked = true;
            Check(options.UpNextEnabled && options.PauseNotifyEnabled && options.PlayNotifyEnabled, "开启统一开关立即恢复三类播放提示");
            var mixedPlayback = new IslandOptions { UpNextEnabled = false, PauseNotifyEnabled = false, PlayNotifyEnabled = true }.Normalized();
            Check(mixedPlayback.UpNextEnabled && mixedPlayback.PauseNotifyEnabled && mixedPlayback.PlayNotifyEnabled,
                "旧版部分开启的播放提示合并为一个开启设置");
            PrivateCall(settings, "SelectPageByTag", "appearance"); PumpAnimations(240); root.UpdateLayout();
            var appearance = (StackPanel)settings.FindName("Page_Appearance");
            Border CardOf(FrameworkElement element)
            {
                DependencyObject? node = element;
                while (node != null)
                {
                    if (node is Border card && ReferenceEquals(card.Style, settings.FindResource("W11SettingCard"))) return card;
                    node = LogicalTreeHelper.GetParent(node);
                }
                throw new InvalidOperationException("设置项没有独立卡片：" + element.Name);
            }
            var visualCard = CardOf((FrameworkElement)settings.FindName("GlassOpacitySlider"));
            var audioSection = (StackPanel)settings.FindName("AppearanceAudioBranch");
            Check(audioSection.TranslatePoint(new System.Windows.Point(), appearance).Y
                - visualCard.TranslatePoint(new System.Windows.Point(0, visualCard.ActualHeight), appearance).Y >= 23,
                "视觉效果卡片与音频分组之间保留清楚的间距");
            var effectCard = CardOf((FrameworkElement)settings.FindName("ReduceEffectsToggle"));
            double cardGap = visualCard.TranslatePoint(new System.Windows.Point(), appearance).Y
                - effectCard.TranslatePoint(new System.Windows.Point(0, effectCard.ActualHeight), appearance).Y;
            Check(!ReferenceEquals(effectCard, visualCard) && cardGap >= 7,
                "削减动效与不透明度各自成卡，渲染后保留间隔");
            foreach (string tag in new[] { "general", "appearance", "media", "behavior", "notifications", "system", "assistant", "focus", "ai" })
            {
                PrivateCall(settings, "SelectPageByTag", tag); PumpAnimations(240); root.UpdateLayout();
                var selectedPage = Descendants<StackPanel>(root).Single(p => p.Name == "Page_" + (tag == "ai" ? "Ai" : char.ToUpperInvariant(tag[0]) + tag[1..]));
                var rows = Descendants<Grid>(selectedPage).Where(row => ReferenceEquals(row.Style, settings.FindResource("W11Row"))).ToArray();
                Check(rows.Length > 0 && rows.Select(CardOf).Distinct().Count() == rows.Length,
                    "每个设置项独立成卡 " + tag);
            }
            var search = (TextBox)settings.FindName("SearchBox");
            var searchBorder = (Border)settings.FindName("SearchBorder");
            double searchY = searchBorder.TranslatePoint(new System.Windows.Point(), root).Y;
            PrivateCall(settings, "ApplyNavExpanded", false, false); root.UpdateLayout();
            Check(search.IsVisible && searchBorder.ActualWidth >= 200
                && Math.Abs(searchBorder.TranslatePoint(new System.Windows.Point(searchBorder.ActualWidth / 2, 0), root).X - root.ActualWidth / 2) <= 1
                && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(searchBorder),
                "收起导航后顶部搜索保持居中、可见且可交互");
            search.Text = "ClipboardSensitiveGuardEnabled"; root.UpdateLayout();
            var match = items.Single(item => item.Visibility == Visibility.Visible);
            match.IsChecked = true; PumpAnimations(240); root.UpdateLayout();
            Check(Equals(match.Tag, "behavior") && ((FrameworkElement)settings.FindName("Page_Behavior")).IsVisible,
                "收起导航时搜索并选择结果仍能打开对应设置页");
            search.Text = "";
            var contentScroll = (ScrollViewer)settings.FindName("SettingsContentScroll");
            contentScroll.ScrollToEnd(); root.UpdateLayout();
            Check(Math.Abs(searchBorder.TranslatePoint(new System.Windows.Point(), root).Y - searchY) <= 1,
                "内容滚动不影响顶部搜索的位置");
            SaveThemePreview(root, "bin/settings-v120-previews/navigation-collapsed.png");
            root.Width = 780; root.Height = 540; root.UpdateLayout();
            Check(searchBorder.ActualWidth >= 200
                && Math.Abs(searchBorder.TranslatePoint(new System.Windows.Point(searchBorder.ActualWidth / 2, 0), root).X - root.ActualWidth / 2) <= 1,
                "最小窗口尺寸下搜索框仍居中且不挤压窗口按钮");
            SaveThemePreview(root, "bin/settings-v120-previews/minimum-window.png");
            root.ClearValue(FrameworkElement.WidthProperty); root.ClearValue(FrameworkElement.HeightProperty);
            PrivateCall(settings, "ApplyNavExpanded", true, false); root.UpdateLayout();
            foreach (var (key, page) in new[] { ("ClipboardSensitiveGuardEnabled", "behavior"), ("AgentDesktopToolsEnabled", "assistant"), ("StartupEnabled", "general") })
                Check(SettingsRegistry.Find(key)?.PageId == page, "注册和搜索分类一致 " + key);
            var net = settings.FindName("NetSpeedThresholdBox") as TextBox;
            var power = settings.FindName("PowerNotifyWattsBox") as TextBox;
            Check(net != null && power != null, "网速与功率阈值使用精确输入框");
            net!.Text = "12.375"; power!.Text = "25.625";
            Check(Math.Abs(SettingsRegistry.GetDouble(options, "NetSpeedThresholdMbps", 0) - 12.375) < .00001 && Math.Abs(SettingsRegistry.GetDouble(options, "PowerNotifyWatts", 0) - 25.625) < .00001, "小数阈值立即生效且不丢失精度");
            net.Text = "-"; ((CheckBox)settings.FindName("FileTransferToggle")).IsChecked = false;
            Check(Math.Abs(SettingsRegistry.GetDouble(options, "NetSpeedThresholdMbps", 0) - 12.375) < .00001, "未完成输入不重置原阈值");
            net.Text = "12.375"; PumpAnimations(450);
            Check(settings.FindName("SettingsSaveStatusText") is TextBlock status && status.Text == "已保存", "保存后显示已保存提示");
            string savedPath = System.IO.Path.Combine(testRoot, "consolidated-options.json");
            Check(options.TrySave(savedPath, true) && Math.Abs(SettingsRegistry.GetDouble(IslandOptions.Load(savedPath), "PowerNotifyWatts", 0) - 25.625) < .00001,
                "实际原子保存后重读仍保留小数阈值");
            Check(!options.TrySave(testRoot, true), "写入失败返回失败，不能误报保存成功");
            settings.ReportSettingsSaved(false);
            Check(((TextBlock)settings.FindName("SettingsSaveStatusText")).Text == "保存失败，请重试", "保存失败展示明确提示");
            settings.ReportSettingsSaved(true);
            Check(Descendants<TextBlock>((StackPanel)settings.FindName("Page_Appearance")).Count(text => text.Text == "削减动效") == 1, "动效开关不重复显示同名介绍");
            Check(Descendants<TextBlock>((StackPanel)settings.FindName("Page_Focus")).Count(text => text.Text == "番茄钟") == 1, "番茄钟不重复显示同名标题");
            var legacy = JsonSerializer.Deserialize<IslandOptions>("""
                {"KeepCompactWhilePlaying":true,"ClipboardAutoCollapseSeconds":5,"LiquidGlassBackdrop":false,"GelAnimation":false,"TwoStepExpand":false,"ShowCover":false,
                 "Extras":{"ReduceMotionEnabled":true,"PowerSaveEnabled":false,"NotificationQQEnabled":false,"NotificationWeChatEnabled":true,"AirplaneBarEnabled":false,"AirplaneNotifyEnabled":false,"plugin/vivo.office/enabled":true}}
                """)!.Normalized();
            Check(SettingsRegistry.GetBool(legacy, "ReduceEffectsEnabled", false) && SettingsRegistry.GetBool(legacy, "NotificationMessengerEnabled", false) && !legacy.Extras.ContainsKey("AirplaneDisplayEnabled"), "旧动效和消息设置保留选择，已移除的飞行模式开关不再保留");
            string json = JsonSerializer.Serialize(legacy);
            foreach (string key in new[] { "KeepCompactWhilePlaying", "ClipboardAutoCollapseSeconds", "LiquidGlassBackdrop", "GelAnimation", "TwoStepExpand", "ShowCover", "ReduceMotionEnabled", "PowerSaveEnabled", "Cover3DEnabled", "NotificationQQEnabled", "NotificationWeChatEnabled", "AirplaneDisplayEnabled", "AirplaneBarEnabled", "AirplaneNotifyEnabled", "TrafficMonitorLinkEnabled" })
                Check(!json.Contains('"' + key + '"'), "配置不再保存冗余键 " + key);
            Check(SettingsRegistry.GetBool(legacy, "plugin/vivo.office/enabled", false), "迁移保留插件设置");
            foreach (string tag in new[] { "general", "appearance", "assistant", "behavior", "media", "system", "focus" })
            {
                PrivateCall(settings, "SelectPageByTag", tag); PumpAnimations(240); root.UpdateLayout();
                var selected = items.Single(item => item.IsChecked == true);
                var indicator = (Border)settings.FindName("NavSelectionIndicator");
                double expectedY = selected.TranslatePoint(new System.Windows.Point(0, (selected.ActualHeight - indicator.Height) / 2), (UIElement)settings.FindName("NavItemsSurface")).Y;
                Check(Math.Abs(((System.Windows.Media.TranslateTransform)indicator.RenderTransform).Y - expectedY) <= 1,
                    "搜索与收起导航后选中标记仍准确跟随页面 " + tag);
                SaveThemePreview(root, System.IO.Path.Combine("bin/settings-v120-previews", tag + ".png"));
            }
            PrivateCall(settings, "SelectPageByTag", "lyrics"); PumpAnimations(240); root.UpdateLayout();
            Check(((FrameworkElement)settings.FindName("AppearanceLyricsBranch")).IsVisible
                && ((FrameworkElement)settings.FindName("Page_Appearance")).Visibility == Visibility.Visible,
                "旧歌词入口自动定位到直接展示的歌词分组");
            var scroll = (ScrollViewer)settings.FindName("SettingsContentScroll");
            scroll.ScrollToEnd(); root.UpdateLayout();
            SaveThemePreview(root, "bin/settings-v120-previews/lyrics-expanded.png");
            PrivateCall(settings, "SelectPageByTag", "system"); PumpAnimations(240); root.UpdateLayout();
            power.BringIntoView(); PumpAnimations(30); root.UpdateLayout();
            SaveThemePreview(root, "bin/settings-v120-previews/system-threshold.png");
            scroll.ScrollToEnd(); root.UpdateLayout();
            SaveThemePreview(root, "bin/settings-v120-previews/system-bottom.png");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
