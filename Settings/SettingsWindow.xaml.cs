using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;
using BitmapCacheOption = System.Windows.Media.Imaging.BitmapCacheOption;
using BitmapCreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions;
using BitmapImage = System.Windows.Media.Imaging.BitmapImage;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using RadioButton = System.Windows.Controls.RadioButton;
using Stretch = System.Windows.Media.Stretch;

namespace alpha_island.Settings
{
    public partial class SettingsWindow : Window
    {
        public event EventHandler<IslandOptions>? OptionsChanged;
        public event EventHandler? ResetRequested;

        private IslandOptions _options = new();
        private bool _suppress = true;
        private bool _settingsEdited;
        private readonly DispatcherTimer _saveDebounceTimer;

        /// <summary>左侧导航栏是否处于展开态（收起时只留图标，不显示文字）。</summary>
        private bool _navExpanded = true;

        /// <summary>「关于」页里异步能力（无线电/出网）还没探完时的有限次补刷。</summary>
        private DispatcherTimer? _aboutRetryTimer;
        private int _aboutRetryTicks;

        private const double NavWidthExpanded = 244.0;
        private const double NavWidthCollapsed = 48.0;

        /// <summary>
        /// 上一轮注册表绑定命中的控件数 / XAML 里找不到的控件数（只统计声明了 ControlName 的项）。
        /// 每次 LoadFrom 打一行日志，加设置项时忘了改 XAML 会直接暴露成"缺失 N"。
        /// </summary>
        private int _registryBound;
        private int _registryMissing;

        /// <summary>置为 true 后 Close() 会真的关窗（正常点 X 只是隐藏）。退出程序时必须先置位。</summary>
        public bool ForceClose { get; set; }

        private readonly bool _persistSettings;
        private readonly string? _aiSecretsFile;
        private int _foregroundSequence;
        public SettingsWindow() : this(true) { }
        internal SettingsWindow(bool persistSettings, string? aiSecretsFile = null, ChatService? connectionService = null)
        {
            _persistSettings = persistSettings;
            _aiSecretsFile = aiSecretsFile ?? (persistSettings ? AppPaths.AiSecretsFile : null);
            _aiConnectionService = connectionService ?? new ChatService();
            InitializeComponent();
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is false) { _foregroundSequence++; Topmost = false; CancelAiConnection(); }
            };
            StateChanged += (_, _) => Topmost = IsVisible && WindowState != WindowState.Minimized;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || e.Handled) return;
                Close(); e.Handled = true;
            };
            InitializeSettingsMotion();
            AddNotificationSettings();
            AddPomodoroSettings();

            _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _saveDebounceTimer.Tick += (_, __) =>
            {
                _saveDebounceTimer.Stop();
                SaveOptions();
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

            if (GreetingSecondsSlider != null && GreetingSecondsValue != null)
            {
                GreetingSecondsValue.Text = $"{GreetingSecondsSlider.Value:0.#}s";
            }

            if (NetSpeedThresholdSlider != null && NetSpeedThresholdValue != null)
            {
                NetSpeedThresholdValue.Text = $"{(int)Math.Round(NetSpeedThresholdSlider.Value)} MB/s";
            }

            _suppress = false;

            // 调试钩子（与 ALPHA_ISLAND_STATE / ALPHA_ISLAND_OPEN_SETTINGS 同一套路）：
            // ALPHA_ISLAND_SETTINGS_PAGE=about 直接落到指定页，方便脚本化截图验收。不影响正常启动。
            string? forcedPage = Environment.GetEnvironmentVariable("ALPHA_ISLAND_SETTINGS_PAGE");
            if (!string.IsNullOrWhiteSpace(forcedPage))
            {
                IslandLog.Debug("Settings", $"调试钩子选择页面 = {forcedPage.Trim()}（导航项 {NavPanel?.Children.Count ?? -1} 个）");
                SelectPageByTag(forcedPage.Trim());
            }

            // 调试钩子：ALPHA_ISLAND_SETTINGS_RESET=1 时打开窗口 2.5 秒后自动点一次「恢复默认」，
            // 用来脚本化验证「恢复默认」整条链路（重置 options → 落盘 → OptionsChanged → 主窗清理运行时状态）。
            if (Environment.GetEnvironmentVariable("ALPHA_ISLAND_SETTINGS_RESET") == "1")
            {
                var resetTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2.5)
                };
                resetTimer.Tick += (_, __) =>
                {
                    resetTimer.Stop();
                    IslandLog.Debug("Settings", "调试钩子：自动触发「恢复默认」");
                    ResetDefaultsButton?.RaiseEvent(
                        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                };
                resetTimer.Start();
            }

            // C 组①⑤：如果用户放了自定义图标（%LOCALAPPDATA%\alpha-island\config\icons\*.png 或 exe 旁 Assets\icons\*.png），
            // 就把对应图标槽里的默认 Segoe 字形换成图片；没有自定义文件时什么都不做（内置字形照旧）。
            ApplyCustomSettingIcons();
        }

        /// <summary>图标槽（XAML 里 32x32 的具名 Border，名字以 Icon_ 开头）带自定义图片时的替换实现。</summary>
        private int ApplyCustomSettingIcons()
        {
            string[] dirs =
            {
                AppPaths.UserIconsDir,
                Path.Combine(AppContext.BaseDirectory, "Assets", "icons")
            };

            int applied = 0;
            foreach (var tile in EnumerateIconTiles(RootGrid))
            {
                string name = tile.Name.Substring("Icon_".Length);
                string? file = null;
                foreach (string dir in dirs)
                {
                    string candidate = Path.Combine(dir, name + ".png");
                    if (File.Exists(candidate)) { file = candidate; break; }
                }
                if (file == null) continue;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.UriSource = new Uri(file, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    tile.Child = new Image
                    {
                        Source = bitmap,
                        Width = 20,
                        Height = 20,
                        Stretch = Stretch.Uniform
                    };
                    tile.Background = Brushes.Transparent;
                    applied++;
                }
                catch (Exception ex)
                {
                    IslandLog.Warn("Settings", $"自定义图标 {name} 加载失败: {ex.Message}");
                }
            }

            if (applied > 0)
            {
                IslandLog.Debug("Settings", $"设置页自定义图标 = {applied} 个（查找目录 {string.Join(" | ", dirs)}）");
            }
            return applied;
        }

        private static IEnumerable<Border> EnumerateIconTiles(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Border border && border.Name.StartsWith("Icon_", StringComparison.Ordinal))
                {
                    yield return border;
                }
                foreach (object? child in LogicalTreeHelper.GetChildren(node))
                {
                    if (child is DependencyObject dependencyObject) stack.Push(dependencyObject);
                }
            }
        }

        /// <summary>按 Tag 选中左侧导航项；找不到就保持当前页。</summary>
        private void SelectPageByTag(string tag)
        {
            if (NavPanel == null) return;

            foreach (object child in NavPanel.Children)
            {
                if (child is RadioButton item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsChecked = true;
                    return;
                }
            }
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
            if (GreetingSecondsSlider != null)
            {
                GreetingSecondsSlider.Value = _options.GreetingSeconds;
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
            if (NetSpeedThresholdSlider != null)
            {
                double mbps = SettingsRegistry.GetDouble(_options, "NetSpeedThresholdMbps", 10.0);
                NetSpeedThresholdSlider.Value = mbps;
                if (NetSpeedThresholdValue != null)
                {
                    NetSpeedThresholdValue.Text = $"{(int)Math.Round(mbps)} MB/s";
                }
            }
            if (NetSpeedEnabledToggle != null)
            {
                NetSpeedEnabledToggle.IsChecked = SettingsRegistry.GetBool(_options, "NetSpeedEnabled", true);
            }
            if (NetSpeedNotifyToggle != null)
            {
                NetSpeedNotifyToggle.IsChecked = SettingsRegistry.GetBool(_options, "NetSpeedNotifyEnabled", true);
            }

            // 注册表驱动的项（含新功能的 Extras）统一回填；必须在 _suppress 复位之前，
            // 否则回填过程会触发 Setting_Changed/Slider_Changed 把值又写回去。
            LoadRegistryControls();
            RefreshAiKeyState();
            RefreshAiModelConnection();

            _suppress = false;
            UpdateEnabledStates();
            RebuildPluginRows();

            IslandLog.Debug("Settings",
                $"设置回填：注册表 {SettingsRegistry.Count} 项，控件命中 {_registryBound}，XAML 缺失 {_registryMissing}，Extras {_options.Extras?.Count ?? 0} 键");
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
            // 注册表声明的滑杆（新增项）走通用数值文本刷新。
            // 注意：文本刷新必须在下面 `if (_suppress) return;` 之前执行 —— 回填时也要显示数值，
            // 这是历史 bug（NullReferenceException + 数值不显示）留下的纪律，别把这段挪到后面。
            if (sender is Slider registrySlider && !string.IsNullOrEmpty(registrySlider.Name))
            {
                var descriptor = SettingsRegistry.FindByControl(registrySlider.Name);
                if (descriptor != null)
                {
                    UpdateRegistryValueText(descriptor, registrySlider.Value);
                }
            }

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
            else if (ReferenceEquals(sender, GreetingSecondsSlider))
            {
                if (GreetingSecondsValue != null)
                {
                    GreetingSecondsValue.Text = $"{e.NewValue:0.#}s";
                }
            }
            else if (ReferenceEquals(sender, NetSpeedThresholdSlider))
            {
                if (NetSpeedThresholdValue != null)
                {
                    NetSpeedThresholdValue.Text = $"{(int)Math.Round(e.NewValue)} MB/s";
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
            _settingsEdited = true;
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
            if (GreetingSecondsSlider != null)
            {
                _options.GreetingSeconds = Math.Clamp(GreetingSecondsSlider.Value, 1.5, 10.0);
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
            if (NetSpeedThresholdSlider != null)
            {
                SettingsRegistry.SetExtra(_options, "NetSpeedThresholdMbps", NetSpeedThresholdSlider.Value);
            }
            if (NetSpeedEnabledToggle != null)
            {
                SettingsRegistry.SetExtra(_options, "NetSpeedEnabled", NetSpeedEnabledToggle.IsChecked == true);
            }
            if (NetSpeedNotifyToggle != null)
            {
                SettingsRegistry.SetExtra(_options, "NetSpeedNotifyEnabled", NetSpeedNotifyToggle.IsChecked == true);
            }

            // 注册表驱动的项统一回写（放在手写块之后：两边写的是同一个属性，值一致，幂等）。
            ApplyRegistryControls();
            SettingsRegistry.SetExtra(_options, "AiApiKey", string.Empty);

            UpdateEnabledStates();
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
            OptionsChanged?.Invoke(this, _options);
        }

        private void TextBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            if (ReferenceEquals(sender, AiBaseUrlBox)) ResetAiModelConnection();
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
            if (GreetingSecondsSlider != null)
            {
                GreetingSecondsSlider.IsEnabled = GreetingToggle?.IsChecked == true;
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
            if (NetSpeedNotifyToggle != null)
            {
                NetSpeedNotifyToggle.IsEnabled = NetSpeedEnabledToggle?.IsChecked == true;
            }
            if (NetSpeedThresholdSlider != null)
            {
                NetSpeedThresholdSlider.IsEnabled = NetSpeedEnabledToggle?.IsChecked == true;
            }
            if (NetSpeedThresholdValue != null)
            {
                NetSpeedThresholdValue.IsEnabled = NetSpeedEnabledToggle?.IsChecked == true;
            }

            // 注册表声明的依赖级联：EnabledByKey 指向的开关关掉时，本项控件与数值文本一起置灰。
            // 加带依赖的新设置项不用再回来改这里。
            foreach (var descriptor in SettingsRegistry.All)
            {
                if (string.IsNullOrEmpty(descriptor.EnabledByKey) || string.IsNullOrEmpty(descriptor.ControlName))
                {
                    continue;
                }

                if (FindName(descriptor.ControlName) is not FrameworkElement control)
                {
                    continue;
                }

                bool enabled = true;
                var dependency = SettingsRegistry.Find(descriptor.EnabledByKey);
                if (dependency != null && !string.IsNullOrEmpty(dependency.ControlName) &&
                    FindName(dependency.ControlName) is ToggleButton dependencyControl)
                {
                    enabled = dependencyControl.IsChecked == true;
                }

                control.IsEnabled = enabled;
                if (!string.IsNullOrEmpty(descriptor.ValueTextName) &&
                    FindName(descriptor.ValueTextName) is FrameworkElement valueText)
                {
                    valueText.IsEnabled = enabled;
                }
            }
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var tag = (sender as RadioButton)?.Tag as string;
            if (tag == null) return;

            // 页面清单是唯一真源。老实现是"六个 case 各自手写六行 Visibility + 一段只查六个页的护栏"，
            // 加第 7、8 页（system / ai）时没跟着改 ⇒ tag 落进 default 直接 return，
            // 点「系统状态」「AI / Agent」什么都不发生（而搜索框照样能搜到这两项）。
            // 现在只维护这张表：新增页在这里加一行就够了，护栏不可能再漏。
            var pages = new (string Tag, FrameworkElement? Page)[]
            {
                (SettingsPageIds.Appearance, Page_Appearance),
                (SettingsPageIds.Island, Page_Island),
                (SettingsPageIds.Audio, Page_Audio),
                (SettingsPageIds.Lyrics, Page_Lyrics),
                (SettingsPageIds.Behavior, Page_Behavior),
                (SettingsPageIds.System, Page_System),
                (SettingsPageIds.Ai, Page_Ai),
                (SettingsPageIds.Plugins, Page_Plugins),
                (SettingsPageIds.About, Page_About),
            };

            FrameworkElement? target = null;
            foreach (var (pageTag, page) in pages)
            {
                // 构造期 XAML 还没建好时字段可能是 null：那一项当作"不存在"，不影响别的页。
                if (page != null && string.Equals(pageTag, tag, StringComparison.Ordinal)) target = page;
            }

            if (target == null) return;

            TransitionSettingsPage(target, pages);

            // 「关于」页每次切进来都重读一遍：版本/能力/服务/日志都是只读的即时信息。
            if (tag == SettingsPageIds.About) RefreshDiagnostics();

            // 一行可断言的诊断：设置页切换落地了没有（脚本用 ALPHA_ISLAND_SETTINGS_PAGE 验收时看它）。
            IslandLog.Debug("Settings", $"设置页 → {tag}（{target.Name ?? "?"} 可见，其余 {pages.Length - 1} 页收起）");
        }

        /// <summary>
        /// 刷新「关于」页的只读诊断区块。幂等；能力探测直接吃 <see cref="CapabilityProbe"/> 的缓存
        /// （异步能力还没探完就先显示「未探测」，<see cref="CapabilityProbe.Changed"/> 回来时再补一次）。
        /// </summary>
        private void RefreshDiagnostics()
        {
            if (AboutText != null)
            {
                try { AboutText.Text = DiagnosticsReport.EnvironmentText(); }
                catch (Exception ex) { AboutText.Text = "环境信息读取失败：" + ex.Message; }
            }

            RefreshCapabilityText();
            ScheduleCapabilityRetry();

            if (ServiceText != null)
            {
                string text = DiagnosticsReport.RegisteredSectionText("服务状态");
                ServiceText.Text = string.IsNullOrWhiteSpace(text) ? "服务宿主尚未接入（阶段 3.3）。" : text;
            }

            if (LogText != null)
            {
                try { LogText.Text = DiagnosticsReport.RecentLogText(120); }
                catch (Exception ex) { LogText.Text = "日志读取失败：" + ex.Message; }
            }

            // 设置窗口没有自动化测试，出问题时只能靠这一行判断「关于」页到底填了多少内容。
            IslandLog.Debug("Settings",
                $"关于页刷新 env={AboutText?.Text.Length ?? -1} cap={CapabilityText?.Text.Length ?? -1} " +
                $"svc={ServiceText?.Text.Length ?? -1} log={LogText?.Text.Length ?? -1}");
        }

        private void RefreshCapabilityText()
        {
            if (CapabilityText == null) return;
            try { CapabilityText.Text = DiagnosticsReport.CapabilityText(); }
            catch (Exception ex) { CapabilityText.Text = "能力检测失败：" + ex.Message; }
        }

        /// <summary>
        /// 无线电/出网这两项是异步探测，启动后几百毫秒才有结论；这里做有限次补刷（约 6 秒上限）。
        /// 只在「关于」页可见时跑，且 <see cref="CapabilityProbe.Check"/> 内部有 in-flight 保护不会重复打网络。
        /// </summary>
        private void ScheduleCapabilityRetry()
        {
            if (!HasUnknownCapability()) return;

            _aboutRetryTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };

            _aboutRetryTimer.Tick -= OnAboutRetryTick;
            _aboutRetryTimer.Tick += OnAboutRetryTick;
            _aboutRetryTicks = 0;
            _aboutRetryTimer.Start();
        }

        private void OnAboutRetryTick(object? sender, EventArgs e)
        {
            _aboutRetryTicks++;

            bool onPage = Page_About != null && Page_About.Visibility == Visibility.Visible;
            if (onPage) RefreshCapabilityText();

            if (_aboutRetryTicks > 9 || !onPage || !HasUnknownCapability())
            {
                _aboutRetryTimer?.Stop();
                _aboutRetryTicks = 0;
            }
        }

        private static bool HasUnknownCapability()
        {
            try
            {
                foreach (CapabilityResult result in CapabilityProbe.CheckAll())
                {
                    if (result.Status == CapabilityStatus.Unknown) return true;
                }
            }
            catch
            {
                // 探测出问题不该把设置窗口弄崩：当作已探完。
            }

            return false;
        }

        private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(DiagnosticsReport.BuildText());
                if (DiagnosticsHint != null) DiagnosticsHint.Text = "报告已复制到剪贴板（" + DateTime.Now.ToString("HH:mm:ss") + "）";
            }
            catch (Exception ex)
            {
                if (DiagnosticsHint != null) DiagnosticsHint.Text = "复制失败：" + ex.Message;
            }
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureRoot();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.Root,
                    UseShellExecute = true,
                });
                if (DiagnosticsHint != null) DiagnosticsHint.Text = "已打开 " + AppPaths.Root;
            }
            catch (Exception ex)
            {
                if (DiagnosticsHint != null) DiagnosticsHint.Text = "打开目录失败：" + ex.Message;
            }
        }

        private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExportDiagnosticsButton != null) ExportDiagnosticsButton.IsEnabled = false;
            try
            {
                string? zip = DiagnosticsReport.Export();
                if (DiagnosticsHint != null)
                    DiagnosticsHint.Text = zip == null ? "导出失败，原因见日志。" : "已导出：" + zip;
            }
            finally
            {
                if (ExportDiagnosticsButton != null) ExportDiagnosticsButton.IsEnabled = true;
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

        /// <summary>用户主动打开设置时，提升到置顶层并恢复键盘操作；隐藏后释放置顶。</summary>
        internal void ShowInForeground(bool requestFocus = true)
        {
            int sequence = ++_foregroundSequence;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            RaiseToFront();
            if (!requestFocus) return;
            FocusWindow();
            // 托盘菜单关闭后系统可能把焦点交回原全屏窗口，排队补一次主动打开操作。
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (sequence != _foregroundSequence || !IsVisible || WindowState == WindowState.Minimized) return;
                RaiseToFront(); FocusWindow();
            }));
        }

        private void RaiseToFront() { Topmost = false; Topmost = true; }
        private void FocusWindow()
        {
            Activate();
            alpha_island.Interop.NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Focus();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
            int version = ++_navMotionVersion;
            if (!animate || !AnimateSettings)
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
                if (version != _navMotionVersion) return;
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
                // 注册表里的标题/说明/键一起进索引：搜"网速""内存""API Key"能直接定位到对应设置页，
                // 不用每个页的导航项自己维护关键词清单。
                hay += " " + SettingsRegistry.SearchWordsForPage(item.Tag as string);
                item.Visibility = q.Length == 0 || hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            _settingsEdited = true;
            _saveDebounceTimer.Stop();
            _suppress = true;
            LoadFrom(new IslandOptions());
            _suppress = false;
            _options.Normalized();
            SaveOptions();
            OptionsChanged?.Invoke(this, _options);
            ResetRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 统一保存入口：先规范化（补齐 Extras 里的默认值、夹取范围），再写盘。
        /// 直接 <c>_options.Save()</c> 会把「还没规范化、Extras 为空的」对象写下去 ——
        /// 恢复默认时就会把整批新功能默认值从 options.json 里抹掉（实测踩过）。
        /// </summary>
        private void SaveOptions()
        {
            if (!_persistSettings) return;
            try
            {
                _options.Normalized().Save(allowRecovery: _settingsEdited);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Settings", "设置保存失败: " + ex.Message);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            CancelAiConnection();
            _saveDebounceTimer.Stop();
            SaveOptions();
            if (ForceClose) return;
            e.Cancel = true;
            Hide();
        }

        // ================= 注册表驱动的通用绑定（阶段 3.5） =================
        // 加设置项的正确姿势：Core/SettingsRegistry.cs 里加一条描述符（含 ControlName），
        // SettingsWindow.xaml 里加一个同名控件。回填、回写、范围夹取、置灰级联、搜索命中断
        // 都在下面统一处理，不用再往 LoadFrom/ApplyChanges/UpdateEnabledStates 里塞手写分支。

        private void LoadRegistryControls()
        {
            _registryBound = 0;
            _registryMissing = 0;

            foreach (var descriptor in SettingsRegistry.All)
            {
                if (string.IsNullOrEmpty(descriptor.ControlName))
                {
                    continue; // 还没有 UI 的设置项（值仍在 options.json 里）
                }

                if (FindName(descriptor.ControlName) is not FrameworkElement control)
                {
                    _registryMissing++;
                    IslandLog.Warn("Settings", $"注册表项 {descriptor.Key} 声明的控件 {descriptor.ControlName} 在 XAML 里不存在");
                    continue;
                }

                _registryBound++;
                object? value = SettingsRegistry.Read(_options, descriptor.Key);
                SetControlValue(control, value);
                UpdateRegistryValueText(descriptor, value);
            }
        }

        private void ApplyRegistryControls()
        {
            foreach (var descriptor in SettingsRegistry.All)
            {
                if (descriptor.IsSecret || string.IsNullOrEmpty(descriptor.ControlName))
                {
                    continue;
                }

                if (FindName(descriptor.ControlName) is not FrameworkElement control)
                {
                    continue;
                }

                if (descriptor.Key == "AiModel" && AiModelBox.SelectedItem is not string) continue;
                SettingsRegistry.Write(_options, descriptor.Key, ReadControlValue(control));
            }
        }

        private void UpdateRegistryValueText(SettingDescriptor descriptor, object? value)
        {
            if (string.IsNullOrEmpty(descriptor.ValueTextName))
            {
                return;
            }

            if (FindName(descriptor.ValueTextName) is TextBlock text)
            {
                text.Text = descriptor.FormatValue(value);
            }
        }

        private static void SetControlValue(FrameworkElement control, object? value)
        {
            switch (control)
            {
                case Slider slider:
                {
                    double v = SettingDescriptor.ToDouble(value, slider.Minimum);
                    if (double.IsNaN(v) || double.IsInfinity(v))
                    {
                        v = slider.Minimum;
                    }
                    slider.Value = Math.Clamp(v, slider.Minimum, Math.Max(slider.Minimum, slider.Maximum));
                    break;
                }
                case ToggleButton toggle:
                    toggle.IsChecked = SettingDescriptor.ToBool(value, false);
                    break;
                case PasswordBox password:
                    // 密钥绝不回填到界面（要换就整条重填）
                    password.Password = string.Empty;
                    break;
                case System.Windows.Controls.TextBox box:
                    box.Text = SettingDescriptor.ToText(value) ?? string.Empty;
                    break;
                case System.Windows.Controls.ComboBox choice:
                    choice.SelectedItem = SettingDescriptor.ToText(value);
                    if (choice.SelectedIndex < 0 && choice.Items.Count > 0) choice.SelectedIndex = 0;
                    break;
            }
        }

        private static object? ReadControlValue(FrameworkElement control) => control switch
        {
            Slider slider => slider.Value,
            ToggleButton toggle => toggle.IsChecked == true,
            PasswordBox password => password.Password,
            System.Windows.Controls.TextBox box => box.Text,
            System.Windows.Controls.ComboBox choice => choice.SelectedItem as string,
            _ => null
        };

        /// <summary>
        /// API Key 的专用收尾（B 组①）：密码框里的明文只走 DPAPI 加密文件，
        /// 顺手把 Extras 里的 <c>AiApiKey</c> 清空 —— 密钥永远不进 options.json。
        /// </summary>
        private void ApplyAiSecret()
        {
            if (FindName("AiApiKeyBox") is not PasswordBox box)
            {
                return;
            }

            string typed = box.Password?.Trim() ?? "";
            if (typed.Length > 0 && _aiSecretsFile != null)
            {
                bool ok = AiSecretStore.TrySave(_aiSecretsFile, typed);
                if (ok)
                {
                    bool wasSuppressed = _suppress;
                    _suppress = true;
                    box.Password = string.Empty;   // 收进加密文件后立刻清掉界面明文
                    _suppress = wasSuppressed;
                    OptionsChanged?.Invoke(this, _options);
                }
                else
                {
                    IslandLog.Warn("Settings", "API Key 保存失败（详见日志）");
                    RefreshAiKeyState();
                    AiApiKeyState.Text = "保存失败，输入已保留（检查配置目录权限）";
                    return;
                }
            }

            SettingsRegistry.SetExtra(_options, "AiApiKey", string.Empty);
            RefreshAiKeyState();
        }

        /// <summary>刷新"密钥已保存/未保存"那行小字。</summary>
        private void RefreshAiKeyState()
        {
            if (FindName("AiApiKeyState") is TextBlock state)
            {
                state.Text = _aiSecretsFile != null && File.Exists(_aiSecretsFile)
                    ? "已加密保存（更换后点击保存）"
                    : "尚未保存（本地模型可留空）";
            }
            if (SaveAiKeyButton != null) SaveAiKeyButton.IsEnabled = _aiSecretsFile != null && !string.IsNullOrWhiteSpace(AiApiKeyBox?.Password);
            if (ClearAiKeyButton != null) ClearAiKeyButton.IsEnabled = _aiSecretsFile != null && File.Exists(_aiSecretsFile);
        }

        private void PasswordBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            RefreshAiModelConnection();
            RefreshAiKeyState();
            if (AiApiKeyState != null && !string.IsNullOrWhiteSpace(AiApiKeyBox.Password)) AiApiKeyState.Text = "尚未保存，请点击保存密钥";
        }

        private void SaveAiKeyButton_Click(object sender, RoutedEventArgs e) => ApplyAiSecret();

        private void ClearAiKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_aiSecretsFile == null || !AiSecretStore.Clear(_aiSecretsFile)) return;
            AiApiKeyBox.Password = "";
            SettingsRegistry.SetExtra(_options, "AiApiKey", string.Empty);
            RefreshAiKeyState();
            RefreshAiModelConnection();
            OptionsChanged?.Invoke(this, _options);
        }

        private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureConfiguration();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ConfigDir) { UseShellExecute = true });
                DiagnosticsHint.Text = "已打开 " + AppPaths.ConfigDir;
            }
            catch (Exception ex) { DiagnosticsHint.Text = "打开配置目录失败：" + ex.Message; }
        }
    }
}
