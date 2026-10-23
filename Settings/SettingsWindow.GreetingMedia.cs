using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private readonly GreetingMediaStore _greetingAssets;
    private GreetingAudioPlayer? _greetingSoundPreview;
    private TextBlock _greetingImageHint = null!, _greetingSoundHint = null!, _greetingMediaStatus = null!;
    private Image _greetingImagePreview = null!;
    private Button _greetingImageReset = null!, _greetingSoundReset = null!;
    private readonly HashSet<string> _retiredGreetingAssets = new(StringComparer.OrdinalIgnoreCase);

    private void AddGreetingMediaSettings()
    {
        var rows = (StackPanel)((Border)((Grid)GreetingToggle.Parent).Parent).Parent;
        var imageActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _greetingImagePreview = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
        imageActions.Children.Add(_greetingImagePreview);
        imageActions.Children.Add(GreetingMediaButton("选择图片", "GreetingImageChoose", () => ChooseGreetingFile(true)));
        _greetingImageReset = GreetingMediaButton("恢复默认", "GreetingImageReset", () => ResetGreetingMedia(true)); imageActions.Children.Add(_greetingImageReset);
        rows.Children.Add(GreetingFileRow("左侧欢迎图片", "PNG、JPG、BMP、ICO；GIF 使用首帧。导入后保存在本机。", imageActions, out _greetingImageHint));

        var soundToggle = new CheckBox { Name = "GreetingSoundToggle" };
        var toggleRow = LaunchSettingRow("\uE767", "欢迎音效", "欢迎提示出现时播放一次，可在下方自选和试听", soundToggle, out _);
        soundToggle.Checked += Setting_Changed; soundToggle.Unchecked += Setting_Changed; rows.Children.Add(SettingCard(toggleRow));
        var soundActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        soundActions.Children.Add(GreetingMediaButton("选择音效", "GreetingSoundChoose", () => ChooseGreetingFile(false)));
        soundActions.Children.Add(GreetingMediaButton("试听", "GreetingSoundPreview", PreviewGreetingSound));
        _greetingSoundReset = GreetingMediaButton("恢复默认", "GreetingSoundReset", () => ResetGreetingMedia(false)); soundActions.Children.Add(_greetingSoundReset);
        rows.Children.Add(GreetingFileRow("启动音效文件", "WAV、MP3、M4A、FLAC、WMA；最长 10 秒。无法读取时可转换成 WAV。", soundActions, out _greetingSoundHint));
        rows.Children.Add(CreateRegisteredSettingRow(SettingsRegistry.Find("GreetingSoundVolume")!));
        _greetingMediaStatus = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8) };
        rows.Children.Add(_greetingMediaStatus);
        IsVisibleChanged += (_, _) => { if (!IsVisible) _greetingSoundPreview?.Stop(); };
        Closed += (_, _) => _greetingSoundPreview?.Dispose();
    }

    private Button GreetingMediaButton(string label, string name, Action action)
    {
        var button = new Button { Name = name, Content = label, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0), MinHeight = 30 };
        button.Click += (_, _) => action(); RegisterName(name, button); return button;
    }

    private Border GreetingFileRow(string title, string hint, FrameworkElement actions, out TextBlock selected)
    {
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        labels.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("W11RowLabel") });
        labels.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        selected = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        labels.Children.Add(selected); row.Children.Add(labels); Grid.SetColumn(actions, 1); row.Children.Add(actions);
        return SettingCard(row);
    }

    private void ChooseGreetingFile(bool image)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = image ? "选择左侧欢迎图片" : "选择启动音效", CheckFileExists = true,
            Filter = image ? "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" : "音效|*.wav;*.mp3;*.m4a;*.flac;*.wma" };
        if (dialog.ShowDialog(this) != true) return;
        try { ImportGreetingMedia(dialog.FileName, image); }
        catch (Exception ex) { _greetingMediaStatus.Text = "文件未更改：" + ex.Message; }
    }

    private void ImportGreetingMedia(string source, bool image)
    {
        if (!image) _greetingSoundPreview?.Stop();
        string key = image ? "GreetingImagePath" : "GreetingSoundPath";
        string prior = SettingsRegistry.GetText(_options, key, "");
        string imported = image ? _greetingAssets.ImportImage(source) : _greetingAssets.ImportSound(source);
        SettingsRegistry.Write(_options, key, imported);
        if (!string.IsNullOrWhiteSpace(prior)) _retiredGreetingAssets.Add(prior);
        _greetingMediaStatus.Text = image ? "图片已导入，原文件移动后仍可使用。" : "音效已导入，可点击试听；原文件移动后仍可使用。";
        RefreshGreetingMediaSettings(); ApplyChanges();
    }

    private void ResetGreetingMedia(bool image)
    {
        if (!image) _greetingSoundPreview?.Stop();
        string key = image ? "GreetingImagePath" : "GreetingSoundPath";
        string prior = SettingsRegistry.GetText(_options, key, ""); SettingsRegistry.Write(_options, key, "");
        if (!string.IsNullOrWhiteSpace(prior)) _retiredGreetingAssets.Add(prior);
        _greetingMediaStatus.Text = image ? "已恢复默认兔子图标。" : "已恢复默认轻柔启动音。";
        RefreshGreetingMediaSettings(); ApplyChanges();
    }

    private void PreviewGreetingSound()
    {
        try
        {
            _greetingSoundPreview ??= new GreetingAudioPlayer(Dispatcher);
            string path = SettingsRegistry.GetText(_options, "GreetingSoundPath", "");
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path)) path = "";
            _greetingSoundPreview.Play(path, SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
            _greetingMediaStatus.Text = SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35) == 0 ? "音效音量为 0，当前静音。" : "正在试听；关闭设置会停止播放。";
        }
        catch (Exception ex) { _greetingMediaStatus.Text = "无法试听：" + ex.Message; }
    }

    private void RefreshGreetingMediaSettings()
    {
        if (_greetingImageHint == null) return;
        string image = SettingsRegistry.GetText(_options, "GreetingImagePath", "");
        string sound = SettingsRegistry.GetText(_options, "GreetingSoundPath", "");
        _greetingImageHint.Text = string.IsNullOrWhiteSpace(image) ? "默认兔子图标" : File.Exists(image) ? "已导入自定义图片" : "图片不可用，显示默认兔子图标";
        _greetingSoundHint.Text = string.IsNullOrWhiteSpace(sound) ? "默认轻柔启动音" : File.Exists(sound) ? "已导入自定义音效" : "音效不可用，使用默认轻柔启动音";
        _greetingImageHint.ToolTip = image; _greetingSoundHint.ToolTip = sound;
        _greetingImageReset.IsEnabled = !string.IsNullOrWhiteSpace(image); _greetingSoundReset.IsEnabled = !string.IsNullOrWhiteSpace(sound);
        try { _greetingImagePreview.Source = string.IsNullOrWhiteSpace(image) ? null : GreetingMediaStore.LoadImage(image); }
        catch { _greetingImagePreview.Source = null; }
    }

    private void UpdateGreetingMediaEnabledStates()
    {
        if (FindName("GreetingSoundToggle") is CheckBox toggle) toggle.IsEnabled = GreetingToggle?.IsChecked == true;
        _greetingSoundPreview?.SetVolume(SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
    }

    private void CleanupRetiredGreetingAssets()
    {
        var active = new[] { SettingsRegistry.GetText(_options, "GreetingImagePath", ""), SettingsRegistry.GetText(_options, "GreetingSoundPath", "") };
        foreach (string file in _retiredGreetingAssets) if (!active.Contains(file, StringComparer.OrdinalIgnoreCase)) _greetingAssets.RemoveOwnedAsset(file);
        _retiredGreetingAssets.Clear();
    }
}
