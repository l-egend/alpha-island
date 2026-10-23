using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace alpha_island.Controls;

internal sealed class VoiceAssistantPill : Grid
{
    internal readonly TextBlock Transcript = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    internal readonly TextBlock Status = new() { FontSize = 10, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis };
    internal readonly MicrophoneWaveView Wave = new() { Width = 66, Height = 27 };
    private readonly StackPanel _words = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 6, 0) };
    internal VoiceAssistantPill()
    {
        Width = 328; Height = 34; VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new() { Width = new GridLength(32) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(76) });
        var microphone = new SemanticIcon { Text = "\uE720", Shape = IconShape.Auto, Width = 24, Height = 24 };
        microphone.SetResourceReference(SemanticIcon.ForegroundProperty, "AccentCyan"); Children.Add(microphone);
        Status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Transcript.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _words.Children.Add(Status); _words.Children.Add(Transcript); SetColumn(_words, 1); Children.Add(_words);
        SetColumn(Wave, 2); Children.Add(Wave);
        Wave.SetResourceReference(MicrophoneWaveView.ForegroundProperty, "AccentCyan");
    }
    internal void Update(VoiceAssistantSnapshot state)
    {
        var problem = state.State == VoiceAssistantState.Error ? state.Problem ?? VoiceRecognitionProblem.FromMessage(state.Hint) : null;
        Status.Text = state.State switch
        {
            VoiceAssistantState.Starting => "语音助手 · 正在唤醒",
            VoiceAssistantState.Recording => "正在聆听 · 停顿自动发送",
            VoiceAssistantState.Finishing => "正在整理语音",
            VoiceAssistantState.Processing => "正在处理指令",
            VoiceAssistantState.Complete => "指令已完成 · 点击查看",
            VoiceAssistantState.Error => problem!.Title,
            _ => "语音助手"
        };
        string text = problem?.Summary ?? (state.State == VoiceAssistantState.Complete ? state.Hint
            : state.Text.Length > 0 ? state.Text : state.Hint);
        Transcript.Text = text.Length > 90 ? "…" + text[^90..] : text;
        ToolTip = new TextBlock { Text = state.Text.Length > 0 ? state.Text + "\n" + state.Hint : state.Hint,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        Wave.Visibility = problem == null ? Visibility.Visible : Visibility.Collapsed;
        SetColumnSpan(_words, problem == null ? 1 : 2);
        Cursor = problem == null ? null : System.Windows.Input.Cursors.Hand;
        if (state.State != VoiceAssistantState.Recording) Wave.Clear();
        Wave.Opacity = state.State == VoiceAssistantState.Recording ? 1 : .25;
    }
}

internal sealed class MicrophoneWaveView : FrameworkElement
{
    internal static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush),
        typeof(MicrophoneWaveView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    internal Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private readonly float[] _levels = new float[MicrophoneSpectrum.Bands];
    internal void SetFrame(float[] levels) { levels.AsSpan(0, Math.Min(levels.Length, _levels.Length)).CopyTo(_levels); InvalidateVisual(); }
    internal void Clear() { Array.Clear(_levels); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double step = ActualWidth / _levels.Length;
        var pen = new Pen(Foreground, Math.Min(2.5, step * .62)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < _levels.Length; i++)
        {
            double height = 2 + Math.Clamp(_levels[i], 0, 1) * (ActualHeight - 2);
            double x = step * (i + .5), y = (ActualHeight - height) / 2;
            drawing.DrawLine(pen, new Point(x, y), new Point(x, y + height));
        }
    }
}
