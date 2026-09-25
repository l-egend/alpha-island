using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using RadioButton = System.Windows.Controls.RadioButton;

namespace alpha_island.Settings
{
    public partial class SettingsWindow : Window
    {
        public event EventHandler<IslandOptions>? OptionsChanged;
        public event EventHandler? ResetRequested;

        private IslandOptions _options = new();
        private bool _suppress = true;
        private readonly DispatcherTimer _saveDebounceTimer;

        /// <summary>左侧导航栏是否处于展开态（收起时只留图标，不显示文字）。</summary>
        private bool _navExpanded = true;

        private const double NavWidthExpanded = 244.0;
        private const double NavWidthCollapsed = 48.0;

        /// <summary>置为 true 后 Close() 会真的关窗（正常点 X 只是隐藏）。退出程序时必须先置位。</summary>
        public bool ForceClose { get; set; }

        public SettingsWindow()
        {
            InitializeComponent();

            _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _saveDebounceTimer.Tick += (_, __) =>
            {
                _saveDebounceTimer.Stop();
                _options.Save();
            };

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - Width) / 2.0;
            Top = workArea.Top + (workArea.Height - Height) / 2.0;

            if (GlassOpacitySlider != null && GlassOpacityValue != null)
            {
                GlassOpacityValue.Text = $"{GlassOpacitySlider.Value:P0}";
            }

            if (SensitivitySlider != null && SensitivityValue != null)
            {
                SensitivityValue.Text = SensitivitySlider.Value.ToString("0.00") + "x";
            }

            if (UpNextSlider != null && UpNextValue != null)
            {
                UpNextValue.Text = $"{UpNextSlider.Value:0}s";
            }

            if (BluetoothNotifySlider != null && BluetoothNotifyValue != null)
            {
                BluetoothNotifyValue.Text = $"{BluetoothNotifySlider.Value:0}s";
            }

            _suppress = false;
        }

        public void LoadFrom(IslandOptions options)
        {
            _suppress = true;
            _options.CopyFrom(options);

            if (GlassOpacitySlider != null)
            {
                GlassOpacitySlider.Value = _options.GlassOpacity;
            }
            if (GlassOpacityValue != null)
            {
                GlassOpacityValue.Text = $"{_options.GlassOpacity:P0}";
            }
            if (LiquidGlassToggle != null)
            {
                LiquidGlassToggle.IsChecked = _options.LiquidGlassBackdrop;
            }
            if (GelAnimationToggle != null)
            {
                GelAnimationToggle.IsChecked = _options.GelAnimation;
            }
            if (TwoStepExpandToggle != null)
            {
                TwoStepExpandToggle.IsChecked = _options.TwoStepExpand;
            }
            if (ShowCoverToggle != null)
            {
                ShowCoverToggle.IsChecked = _options.ShowCover;
            }
            if (VisualizerToggle != null)
            {
                VisualizerToggle.IsChecked = _options.VisualizerEnabled;
            }
            if (ShowWaveToggle != null)
            {
                ShowWaveToggle.IsChecked = _options.ShowWaveLayer;
            }
            if (SensitivitySlider != null)
            {
                SensitivitySlider.Value = _options.Sensitivity;
            }
            if (SensitivityValue != null)
            {
                SensitivityValue.Text = _options.Sensitivity.ToString("0.00") + "x";
            }
            if (LyricsToggle != null)
            {
                LyricsToggle.IsChecked = _options.LyricsEnabled;
            }
            if (TranslationToggle != null)
            {
                TranslationToggle.IsChecked = _options.ShowTranslation;
            }
            if (LyricsOffsetSlider != null)
            {
                LyricsOffsetSlider.Value = _options.LyricsOffsetMs;
            }
            if (LyricsOffsetValue != null)
            {
                int ms = _options.LyricsOffsetMs;
                LyricsOffsetValue.Text = ms > 0 ? $"+{ms}ms" : $"{ms}ms";
            }
            if (KeepCompactToggle != null)
            {
                KeepCompactToggle.IsChecked = _options.KeepCompactWhilePlaying;
            }
            if (UpNextToggle != null)
            {
                UpNextToggle.IsChecked = _options.UpNextEnabled;
            }
            if (UpNextSlider != null)
            {
                UpNextSlider.Value = _options.UpNextSeconds;
            }
            if (PauseNotifyToggle != null)
            {
                PauseNotifyToggle.IsChecked = _options.PauseNotifyEnabled;
            }
            if (PlayNotifyToggle != null)
            {
                PlayNotifyToggle.IsChecked = _options.PlayNotifyEnabled;
            }
            if (VolumeNotifyToggle != null)
            {
                VolumeNotifyToggle.IsChecked = _options.VolumeNotifyEnabled;
            }
            if (GreetingToggle != null)
            {
                GreetingToggle.IsChecked = _options.GreetingEnabled;
            }
            if (GreetingTextBox != null)
            {
                GreetingTextBox.Text = _options.GreetingText;
            }
            if (BluetoothNotifyToggle != null)
            {
                BluetoothNotifyToggle.IsChecked = _options.BluetoothNotifyEnabled;
            }
            if (BluetoothNotifySlider != null)
            {
                BluetoothNotifySlider.Value = _options.BluetoothNotifySeconds;
            }
            if (BluetoothNotifyValue != null)
            {
                BluetoothNotifyValue.Text = $"{_options.BluetoothNotifySeconds:0}s";
            }
            if (BatteryNotifyToggle != null)
            {
                BatteryNotifyToggle.IsChecked = _options.BatteryNotifyEnabled;
            }
            if (BatteryRippleToggle != null)
            {
                BatteryRippleToggle.IsChecked = _options.BatteryRippleEnabled;
            }
            if (ClipboardNotifyToggle != null)
            {
                ClipboardNotifyToggle.IsChecked = _options.ClipboardNotifyEnabled;
            }
            if (ClipboardRightClickPasteToggle != null)
            {
                ClipboardRightClickPasteToggle.IsChecked = _options.ClipboardRightClickPaste;
            }
            if (FileTransferToggle != null)
            {
                FileTransferToggle.IsChecked = _options.FileTransferEnabled;
            }

            _suppress = false;
            UpdateEnabledStates();
        }

        public void UpdateDeviceText(string deviceName)
        {
            if (AudioDeviceText != null)
            {
                AudioDeviceText.Text = deviceName;
            }
        }

        public void UpdateLyricsSource(string source)
        {
            if (LyricsSourceText != null)
            {
                LyricsSourceText.Text = source;
            }
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ReferenceEquals(sender, GlassOpacitySlider))
            {
                if (GlassOpacityValue != null)
                {
                    GlassOpacityValue.Text = $"{e.NewValue:P0}";
                }
            }
            else if (ReferenceEquals(sender, SensitivitySlider))
            {
                if (SensitivityValue != null)
                {
                    SensitivityValue.Text = e.NewValue.ToString("0.00") + "x";
                }
            }
            else if (ReferenceEquals(sender, UpNextSlider))
            {
                if (UpNextValue != null)
                {
                    UpNextValue.Text = $"{e.NewValue:0}s";
                }
            }
            else if (ReferenceEquals(sender, BluetoothNotifySlider))
            {
                if (BluetoothNotifyValue != null)
                {
                    BluetoothNotifyValue.Text = $"{e.NewValue:0}s";
                }
            }
            else if (ReferenceEquals(sender, LyricsOffsetSlider))
            {
                if (LyricsOffsetValue != null)
                {
                    int ms = (int)Math.Round(e.NewValue);
                    LyricsOffsetValue.Text = ms > 0 ? $"+{ms}ms" : $"{ms}ms";
                }
            }

            if (_suppress) return;
            ApplyChanges();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            ApplyChanges();
        }

        private void ApplyChanges()
        {
            if (GlassOpacitySlider != null)
            {
                _options.GlassOpacity = GlassOpacitySlider.Value;
            }
            if (LiquidGlassToggle != null)
            {
                _options.LiquidGlassBackdrop = LiquidGlassToggle.IsChecked == true;
            }
            if (GelAnimationToggle != null)
            {
                _options.GelAnimation = GelAnimationToggle.IsChecked == true;
            }
            if (TwoStepExpandToggle != null)
            {
                _options.TwoStepExpand = TwoStepExpandToggle.IsChecked == true;
            }
            if (ShowCoverToggle != null)
            {
                _options.ShowCover = ShowCoverToggle.IsChecked == true;
            }
            if (VisualizerToggle != null)
            {
                _options.VisualizerEnabled = VisualizerToggle.IsChecked == true;
            }
            if (ShowWaveToggle != null)
            {
                _options.ShowWaveLayer = ShowWaveToggle.IsChecked == true;
            }
            if (SensitivitySlider != null)
            {
                _options.Sensitivity = SensitivitySlider.Value;
            }
            if (LyricsToggle != null)
            {
                _options.LyricsEnabled = LyricsToggle.IsChecked == true;
            }
            if (TranslationToggle != null)
            {
                _options.ShowTranslation = TranslationToggle.IsChecked == true;
            }
            if (LyricsOffsetSlider != null)
            {
                _options.LyricsOffsetMs = (int)Math.Round(LyricsOffsetSlider.Value);
            }
            if (KeepCompactToggle != null)
            {
                _options.KeepCompactWhilePlaying = KeepCompactToggle.IsChecked == true;
            }
            if (UpNextToggle != null)
            {
                _options.UpNextEnabled = UpNextToggle.IsChecked == true;
            }
            if (UpNextSlider != null)
            {
                _options.UpNextSeconds = (int)Math.Round(UpNextSlider.Value);
            }
            if (PauseNotifyToggle != null)
            {
                _options.PauseNotifyEnabled = PauseNotifyToggle.IsChecked == true;
            }
            if (PlayNotifyToggle != null)
            {
                _options.PlayNotifyEnabled = PlayNotifyToggle.IsChecked == true;
            }
            if (VolumeNotifyToggle != null)
            {
                _options.VolumeNotifyEnabled = VolumeNotifyToggle.IsChecked == true;
            }
            if (GreetingToggle != null)
            {
                _options.GreetingEnabled = GreetingToggle.IsChecked == true;
            }
            if (GreetingTextBox != null)
            {
                _options.GreetingText = string.IsNullOrWhiteSpace(GreetingTextBox.Text) ? "欢迎回来" : GreetingTextBox.Text;
            }
            if (BluetoothNotifyToggle != null)
            {
                _options.BluetoothNotifyEnabled = BluetoothNotifyToggle.IsChecked == true;
            }
            if (BluetoothNotifySlider != null)
            {
                _options.BluetoothNotifySeconds = (int)Math.Round(BluetoothNotifySlider.Value);
            }
            if (BatteryNotifyToggle != null)
            {
                _options.BatteryNotifyEnabled = BatteryNotifyToggle.IsChecked == true;
            }
            if (BatteryRippleToggle != null)
            {
                _options.BatteryRippleEnabled = BatteryRippleToggle.IsChecked == true;
            }
            if (ClipboardNotifyToggle != null)
            {
                _options.ClipboardNotifyEnabled = ClipboardNotifyToggle.IsChecked == true;
            }
            if (ClipboardRightClickPasteToggle != null)
            {
                _options.ClipboardRightClickPaste = ClipboardRightClickPasteToggle.IsChecked == true;
            }
            if (FileTransferToggle != null)
            {
                _options.FileTransferEnabled = FileTransferToggle.IsChecked == true;
            }

            UpdateEnabledStates();
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
            OptionsChanged?.Invoke(this, _options);
        }

        private void TextBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ApplyChanges();
        }

        private void UpdateEnabledStates()
        {
            if (TranslationToggle != null)
            {
                TranslationToggle.IsEnabled = LyricsToggle?.IsChecked == true;
            }
            if (LyricsOffsetSlider != null)
            {
                LyricsOffsetSlider.IsEnabled = LyricsToggle?.IsChecked == true;
            }
            if (GreetingTextBox != null)
            {
                GreetingTextBox.IsEnabled = GreetingToggle?.IsChecked == true;
            }
            if (BluetoothNotifySlider != null)
            {
                BluetoothNotifySlider.IsEnabled = BluetoothNotifyToggle?.IsChecked == true;
            }
            if (BatteryRippleToggle != null)
            {
                BatteryRippleToggle.IsEnabled = BatteryNotifyToggle?.IsChecked == true;
            }
            if (ClipboardRightClickPasteToggle != null)
            {
                ClipboardRightClickPasteToggle.IsEnabled = ClipboardNotifyToggle?.IsChecked == true;
            }
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var tag = (sender as RadioButton)?.Tag as string;
            if (tag == null) return;

            if (Page_Appearance == null || Page_Island == null || Page_Audio == null ||
                Page_Lyrics == null || Page_Behavior == null || Page_About == null)
            {
                return;
            }

            switch (tag)
            {
                case "appearance":
                    Page_Appearance.Visibility = Visibility.Visible;
                    Page_Island.Visibility = Visibility.Collapsed;
                    Page_Audio.Visibility = Visibility.Collapsed;
                    Page_Lyrics.Visibility = Visibility.Collapsed;
                    Page_Behavior.Visibility = Visibility.Collapsed;
                    Page_About.Visibility = Visibility.Collapsed;
                    break;
                case "island":
                    Page_Appearance.Visibility = Visibility.Collapsed;
                    Page_Island.Visibility = Visibility.Visible;
                    Page_Audio.Visibility = Visibility.Collapsed;
                    Page_Lyrics.Visibility = Visibility.Collapsed;
                    Page_Behavior.Visibility = Visibility.Collapsed;
                    Page_About.Visibility = Visibility.Collapsed;
                    break;
                case "audio":
                    Page_Appearance.Visibility = Visibility.Collapsed;
                    Page_Island.Visibility = Visibility.Collapsed;
                    Page_Audio.Visibility = Visibility.Visible;
                    Page_Lyrics.Visibility = Visibility.Collapsed;
                    Page_Behavior.Visibility = Visibility.Collapsed;
                    Page_About.Visibility = Visibility.Collapsed;
                    break;
                case "lyrics":
                    Page_Appearance.Visibility = Visibility.Collapsed;
                    Page_Island.Visibility = Visibility.Collapsed;
                    Page_Audio.Visibility = Visibility.Collapsed;
                    Page_Lyrics.Visibility = Visibility.Visible;
                    Page_Behavior.Visibility = Visibility.Collapsed;
                    Page_About.Visibility = Visibility.Collapsed;
                    break;
                case "behavior":
                    Page_Appearance.Visibility = Visibility.Collapsed;
                    Page_Island.Visibility = Visibility.Collapsed;
                    Page_Audio.Visibility = Visibility.Collapsed;
                    Page_Lyrics.Visibility = Visibility.Collapsed;
                    Page_Behavior.Visibility = Visibility.Visible;
                    Page_About.Visibility = Visibility.Collapsed;
                    break;
                case "about":
                    Page_Appearance.Visibility = Visibility.Collapsed;
                    Page_Island.Visibility = Visibility.Collapsed;
                    Page_Audio.Visibility = Visibility.Collapsed;
                    Page_Lyrics.Visibility = Visibility.Collapsed;
                    Page_Behavior.Visibility = Visibility.Collapsed;
                    Page_About.Visibility = Visibility.Visible;
                    break;
                default:
                    return;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>顶部三横线按钮：展开 / 收起左侧导航栏。</summary>
        private void NavCollapse_Click(object sender, RoutedEventArgs e) => ApplyNavExpanded(!_navExpanded, true);

        /// <summary>
        /// 切换导航栏形态。展开 = 244px 图标 + 文字；收起 = 48px 只留图标。
        /// 文字在宽度动画开始前就先隐藏，否则会被面板裁掉一半。
        /// </summary>
        private void ApplyNavExpanded(bool expanded, bool animate)
        {
            _navExpanded = expanded;
            var textVis = expanded ? Visibility.Visible : Visibility.Collapsed;

            if (NavPanel != null)
            {
                foreach (var child in NavPanel.Children)
                {
                    if (child is not RadioButton item) continue;

                    // 导航项内容 = 横向 StackPanel，[0] 是图标，其余都是文字
                    if (item.Content is System.Windows.Controls.Panel content)
                    {
                        for (int i = 1; i < content.Children.Count; i++)
                        {
                            if (content.Children[i] is TextBlock label) label.Visibility = textVis;
                        }
                    }

                    // 收起时内容区左右边距收窄，图标才在 48px 面板里真正居中。
                    // 图标是固定 Width=22 的左对齐 TextBlock，字形墨迹贴在盒子左边，
                    // 所以左距要比右距大 5px 才能让墨迹落在面板中线上。
                    item.ApplyTemplate();
                    if (item.Template.FindName("Presenter", item) is ContentPresenter presenter)
                    {
                        presenter.Margin = expanded ? new Thickness(16, 0, 10, 0) : new Thickness(14, 0, 4, 0);
                    }
                }
            }

            if (SearchBorder != null) SearchBorder.Visibility = textVis;
            if (NavFooter != null) NavFooter.Visibility = textVis;

            if (NavScroll != null)
            {
                NavScroll.Padding = expanded ? new Thickness(10, 0, 10, 10) : new Thickness(4, 0, 4, 10);
            }

            if (NavCollapseButton != null)
            {
                // 注意：本类继承自 Window，自身就有 HorizontalAlignment 属性，
                // 所以这里必须用全限定名，否则 CS0176（把类型名当成了实例属性）。
                NavCollapseButton.HorizontalAlignment = expanded
                    ? System.Windows.HorizontalAlignment.Left
                    : System.Windows.HorizontalAlignment.Center;
                NavCollapseButton.Margin = expanded
                    ? new Thickness(10, 4, 0, 12)
                    : new Thickness(0, 4, 0, 12);
                NavCollapseButton.ToolTip = expanded ? "收起导航栏" : "展开导航栏";
            }

            if (NavPane == null) return;

            double target = expanded ? NavWidthExpanded : NavWidthCollapsed;
            if (!animate)
            {
                NavPane.BeginAnimation(WidthProperty, null);
                NavPane.Width = target;
                return;
            }

            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                target, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            anim.Completed += (_, __) =>
            {
                NavPane.BeginAnimation(WidthProperty, null);
                NavPane.Width = target;
            };
            NavPane.BeginAnimation(WidthProperty, anim);
        }

        /// <summary>Win11 风格导航搜索框：按 Uid/Tag 过滤左侧导航项，只影响可见性，不改当前页。</summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox?.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (NavPanel == null) return;

            string q = (SearchBox?.Text ?? string.Empty).Trim();
            foreach (var child in NavPanel.Children)
            {
                if (child is not RadioButton item) continue;

                string hay = (item.Uid ?? string.Empty) + " " + ((item.Tag as string) ?? string.Empty);
                item.Visibility = q.Length == 0 || hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            _saveDebounceTimer.Stop();
            _suppress = true;
            LoadFrom(new IslandOptions());
            _suppress = false;
            _options.Save();
            OptionsChanged?.Invoke(this, _options);
            ResetRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _saveDebounceTimer.Stop();
            _options.Save();
            if (ForceClose) return;
            e.Cancel = true;
            Hide();
        }
    }
}