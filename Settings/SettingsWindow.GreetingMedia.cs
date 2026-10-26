using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    public event EventHandler? GreetingPreviewRequested;
    internal Func<string>? GreetingPresetResolver { get; set; }
    private string? _greetingRandomPreviewPreset;
    private string GreetingSettingsPreset => GreetingPresetCatalog.GetId(_options) is var id && id == GreetingPresetCatalog.Random
        ? GreetingPresetResolver?.Invoke() ?? (_greetingRandomPreviewPreset ??= GreetingPresetCatalog.ChooseRandomPreset()) : id;

    private readonly GreetingMediaStore _greetingAssets;
    private GreetingAudioPlayer? _greetingSoundPreview;
    private TextBlock _greetingImageHint = null!, _greetingSoundHint = null!, _greetingMediaStatus = null!;
    private Image _greetingImagePreview = null!;
    private Controls.WelcomeRabbitIcon _greetingRabbitPreview = null!;
    private ComboBox _greetingPresetCombo = null!;
    private TextBlock _greetingCustomPresetLabel = null!;
    private bool _greetingPresetUpdating;
    private GreetingGifFrame[] _greetingPreviewFrames = [];
    private Button _greetingImageReset = null!, _greetingSoundReset = null!;
    private readonly HashSet<string> _retiredGreetingAssets = new(StringComparer.OrdinalIgnoreCase);

    private void AddGreetingMediaSettings()
    {
        var rows = (StackPanel)((Border)((Grid)GreetingToggle.Parent).Parent).Parent;
        _greetingPresetCombo = new ComboBox { Name = "GreetingPresetCombo", Width = 210, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center, ItemsSource = GreetingPresetCatalog.Labels };
        RegisterName(_greetingPresetCombo.Name, _greetingPresetCombo);
        _greetingPresetCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _greetingPresetUpdating || _greetingPresetCombo.SelectedIndex < 0) return;
            SelectGreetingPreset(GreetingPresetCatalog.Ids[_greetingPresetCombo.SelectedIndex]);
        };
        var presetActions = new Grid { VerticalAlignment = VerticalAlignment.Center }; presetActions.Children.Add(_greetingPresetCombo);
        _greetingCustomPresetLabel = new TextBlock { Text = "自定义图片与音效", Style = (Style)FindResource("W11RowHint"),
            IsHitTestVisible = false, Margin = new Thickness(12, 0, 28, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        presetActions.Children.Add(_greetingCustomPresetLabel);
        rows.Children.Add(GreetingFileRow("启动欢迎组合", "图片与音效同步切换。", presetActions, out _, "\uE7F4"));
        var imageActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var preview = new Grid { Width = 72, Height = 72, Margin = new Thickness(0, 0, 12, 0) };
        _greetingRabbitPreview = new Controls.WelcomeRabbitIcon { Width = 64, Height = 64, AnimationEnabled = false };
        _greetingImagePreview = new Image { Width = 72, Height = 72, Stretch = Stretch.Uniform };
        preview.Children.Add(_greetingRabbitPreview); preview.Children.Add(_greetingImagePreview);
        imageActions.Children.Add(preview);
        var imageButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var chooseAndTest = new StackPanel { Orientation = Orientation.Horizontal };
        chooseAndTest.Children.Add(GreetingMediaButton("选择图片", "GreetingImageChoose", () => ChooseGreetingFile(true)));
        var test = GreetingMediaButton("临时测试", "GreetingImageTest", PreviewGreeting);
        test.ToolTip = "在 Alpha Island 上播放一次当前欢迎动画，音效遵循现有开关与音量；不更改启动设置。";
        chooseAndTest.Children.Add(test); imageButtons.Children.Add(chooseAndTest);
        _greetingImageReset = GreetingMediaButton("恢复默认", "GreetingImageReset", () => ResetGreetingMedia(true));
        _greetingImageReset.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _greetingImageReset.Margin = new Thickness(4, 6, 0, 0); imageButtons.Children.Add(_greetingImageReset);
        imageActions.Children.Add(imageButtons);
        rows.Children.Add(GreetingFileRow("欢迎图片", "静态预览，点击临时测试播放。支持 PNG、JPG、BMP、ICO、GIF。", imageActions, out _greetingImageHint, "\uEB9F"));

        var soundToggle = new CheckBox { Name = "GreetingSoundToggle" };
        var toggleRow = LaunchSettingRow("\uE767", "欢迎音效", "欢迎提示出现时播放一次，可在下方自选和试听", soundToggle, out _);
        soundToggle.Checked += Setting_Changed; soundToggle.Unchecked += Setting_Changed; rows.Children.Add(SettingCard(toggleRow));
        var soundActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        soundActions.Children.Add(GreetingMediaButton("选择音效", "GreetingSoundChoose", () => ChooseGreetingFile(false)));
        soundActions.Children.Add(GreetingMediaButton("试听", "GreetingSoundPreview", PreviewGreetingSound));
        _greetingSoundReset = GreetingMediaButton("恢复默认", "GreetingSoundReset", () => ResetGreetingMedia(false)); soundActions.Children.Add(_greetingSoundReset);
        rows.Children.Add(GreetingFileRow("启动音效文件", "WAV、MP3、M4A、FLAC、WMA；最长 10 秒。", soundActions, out _greetingSoundHint, "\uE767"));
        rows.Children.Add(CreateRegisteredSettingRow(SettingsRegistry.Find("GreetingSoundVolume")!));
        _greetingMediaStatus = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8) };
        rows.Children.Add(_greetingMediaStatus);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _greetingSoundPreview?.Stop();
        };
        Closed += (_, _) => { _greetingSoundPreview?.Dispose(); _greetingPreviewFrames = []; _greetingImagePreview.Source = null; };
    }

    private Button GreetingMediaButton(string label, string name, Action action)
    {
        var button = new Button { Name = name, Content = label, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0), MinHeight = 30 };
        button.Click += (_, _) => action(); RegisterName(name, button); return button;
    }

    private Border GreetingFileRow(string title, string hint, FrameworkElement actions, out TextBlock selected, string icon = "\uE767")
    {
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new Border { Style = (Style)FindResource("W11IconTile"),
            Child = new AlphaIsland.PluginSdk.GlyphIcon { Style = (Style)FindResource("W11IconGlyph"), Text = icon } });
        var labels = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("W11RowLabel") });
        labels.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        selected = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        labels.Children.Add(selected); Grid.SetColumn(labels, 1); row.Children.Add(labels); Grid.SetColumn(actions, 2); row.Children.Add(actions);
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
        string retained = image ? GreetingPresetCatalog.SoundKey(_options, GreetingSettingsPreset) : GreetingPresetCatalog.ImageKey(_options, GreetingSettingsPreset);
        string imported = image ? _greetingAssets.ImportImage(source) : _greetingAssets.ImportSound(source);
        SettingsRegistry.Write(_options, "GreetingPreset", GreetingPresetCatalog.Custom);
        SettingsRegistry.Write(_options, image ? "GreetingSoundPath" : "GreetingImagePath", retained);
        SettingsRegistry.Write(_options, key, imported);
        if (!string.IsNullOrWhiteSpace(prior)) _retiredGreetingAssets.Add(prior);
        _greetingMediaStatus.Text = image ? "图片已导入，原文件移动后仍可使用。" : "音效已导入，可点击试听；原文件移动后仍可使用。";
        RefreshGreetingMediaSettings(); ApplyChanges();
    }

    private void SelectGreetingPreset(string id)
    {
        _greetingSoundPreview?.Stop();
        foreach (string key in new[] { "GreetingImagePath", "GreetingSoundPath" })
        {
            string prior = SettingsRegistry.GetText(_options, key, "");
            if (!string.IsNullOrWhiteSpace(prior)) _retiredGreetingAssets.Add(prior);
        }
        GreetingPresetCatalog.Select(_options, id);
        _greetingMediaStatus.Text = id switch
        {
            GreetingPresetCatalog.Nailong => "已切换为奶龙动图与欢笑音效。",
            GreetingPresetCatalog.Amiya => "已切换为阿米娅透明动画与问候语音。",
            GreetingPresetCatalog.HoshinoSwimsuit => "已切换为星野透明动画与欢迎语音。",
            GreetingPresetCatalog.Random => "每次启动随机选择一组，图片和语音同步切换。",
            _ => "已切换为兔子图标与轻柔启动音。"
        };
        ApplyChanges(); RefreshGreetingMediaSettings();
    }

    private void ResetGreetingMedia(bool image) => SelectGreetingPreset(GreetingPresetCatalog.Rabbit);

    private void PreviewGreeting()
    {
        _greetingSoundPreview?.Stop();
        GreetingPreviewRequested?.Invoke(this, EventArgs.Empty);
        _greetingMediaStatus.Text = "已发起临时测试；动画播放一次，不更改启动设置。";
    }

    private void PreviewGreetingSound()
    {
        try
        {
            _greetingSoundPreview ??= new GreetingAudioPlayer(Dispatcher);
            string path = GreetingPresetCatalog.SoundKey(_options, GreetingSettingsPreset);
            if (!GreetingPresetCatalog.IsBuiltInSound(path) && !string.IsNullOrWhiteSpace(path) && !File.Exists(path)) path = "";
            _greetingSoundPreview.Play(path, SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
            _greetingMediaStatus.Text = SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35) == 0 ? "音效音量为 0，当前静音。" : "正在试听；关闭设置会停止播放。";
        }
        catch (Exception ex) { _greetingMediaStatus.Text = "无法试听：" + ex.Message; }
    }

    private void RefreshGreetingMediaSettings()
    {
        if (_greetingImageHint == null) return;
        string id = GreetingPresetCatalog.GetId(_options);
        string preset = GreetingSettingsPreset;
        string image = GreetingPresetCatalog.ImageKey(_options, preset), sound = GreetingPresetCatalog.SoundKey(_options, preset);
        _greetingPresetUpdating = true;
        try { _greetingPresetCombo.SelectedIndex = Array.IndexOf(GreetingPresetCatalog.Ids, id); }
        finally { _greetingPresetUpdating = false; }
        _greetingCustomPresetLabel.Visibility = id == GreetingPresetCatalog.Custom ? Visibility.Visible : Visibility.Collapsed;
        _greetingImageHint.Text = image switch
        {
            GreetingPresetCatalog.AmiyaMedia => "内置阿米娅透明动图",
            GreetingPresetCatalog.NailongMedia => "内置奶龙透明动图",
            GreetingPresetCatalog.HoshinoSwimsuitMedia => "内置星野透明动图",
            _ => string.IsNullOrWhiteSpace(image) ? "默认兔子图标" : File.Exists(image) ? "已导入自定义图片" : "图片不可用，显示默认兔子图标"
        };
        if (id == GreetingPresetCatalog.Random) _greetingImageHint.Text = "本次组合：" + GreetingPresetCatalog.Labels[Array.IndexOf(GreetingPresetCatalog.Ids, preset)];
        _greetingSoundHint.Text = sound switch
        {
            GreetingPresetCatalog.AmiyaMedia => "内置阿米娅问候语音",
            GreetingPresetCatalog.NailongMedia => "内置欢笑音效",
            GreetingPresetCatalog.HoshinoSwimsuitMedia => "日配：欢迎回来，老师",
            _ => string.IsNullOrWhiteSpace(sound) ? "默认轻柔启动音" : File.Exists(sound) ? "已导入自定义音效" : "音效不可用，使用默认轻柔启动音"
        };
        _greetingImageHint.ToolTip = image; _greetingSoundHint.ToolTip = sound;
        _greetingImageHint.Visibility = !GreetingPresetCatalog.IsBuiltInAnimation(image) && !string.IsNullOrWhiteSpace(image)
            ? Visibility.Visible : Visibility.Collapsed;
        _greetingSoundHint.Visibility = !GreetingPresetCatalog.IsBuiltInSound(sound) && !string.IsNullOrWhiteSpace(sound)
            ? Visibility.Visible : Visibility.Collapsed;
        _greetingImageReset.IsEnabled = !string.IsNullOrWhiteSpace(image); _greetingSoundReset.IsEnabled = !string.IsNullOrWhiteSpace(sound);
        try { _greetingPreviewFrames = GreetingPresetCatalog.LoadFrames(image, firstFrameOnly: true); }
        catch { _greetingPreviewFrames = []; }
        _greetingImagePreview.Source = _greetingPreviewFrames.Length > 0 ? _greetingPreviewFrames[0].Image : null;
        _greetingImagePreview.Visibility = _greetingPreviewFrames.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _greetingRabbitPreview.Visibility = _greetingPreviewFrames.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
