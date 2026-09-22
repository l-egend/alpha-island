using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        /// <summary>置为 true 后 Close() 会真的关窗（正常点 X 只是隐藏）。退出程序时必须先置位。</summary>
        public bool ForceClose { get; set; }

        public SettingsWindow()
        {
            InitializeComponent();

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

            UpdateEnabledStates();
            _options.Save();
            OptionsChanged?.Invoke(this, _options);
        }

        private void UpdateEnabledStates()
        {
            if (TranslationToggle != null)
            {
                TranslationToggle.IsEnabled = LyricsToggle?.IsChecked == true;
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

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            _suppress = true;
            LoadFrom(new IslandOptions());
            _suppress = false;
            _options.Save();
            OptionsChanged?.Invoke(this, _options);
            ResetRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (ForceClose) return;
            e.Cancel = true;
            Hide();
        }
    }
}