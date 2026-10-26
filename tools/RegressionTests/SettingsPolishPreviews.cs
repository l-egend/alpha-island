using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AlphaIsland.Plugins.ApiChat;
using alpha_island.Core;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void SettingsPolishPreviews(alpha_island.MainWindow main, Grid root,
        alpha_island.Settings.SettingsWindow settings, FrameworkElement settingsRoot)
    {
        string folder = "bin/SettingsPolish168/previews/" + typeof(alpha_island.App).Assembly.GetName().Version!.ToString(3);
        Directory.CreateDirectory(folder);
        PrivateCall(settings, "SelectPageByTag", "assistant");
        settings.SettingsContentScroll.ScrollToHome(); settingsRoot.UpdateLayout();
        var title = Descendants<TextBlock>(settingsRoot).First(text => text.Text == "智能助手" && text.FontSize > 20);
        foreach (double width in new[] { 640d, 780d, 880d, 1100d })
        foreach (bool expanded in new[] { true, false })
        {
            settingsRoot.Width = width; PrivateCall(settings, "ApplyNavExpanded", expanded, false);
            settingsRoot.UpdateLayout(); PumpAnimations(20); settingsRoot.UpdateLayout();
            Console.WriteLine($"Settings layout {width:0}/{expanded}: searchX={settings.SearchBorder.TranslatePoint(new Point(), settingsRoot).X:0.0}, titleX={title.TranslatePoint(new Point(), settingsRoot).X:0.0}, navigation={settings.NavPane.ActualWidth:0.0}, searchFits={settings.SearchBorder.ActualWidth <= settings.SettingsSearchHost.ActualWidth + .5}");
        }
        settingsRoot.Width = 880; PrivateCall(settings, "ApplyNavExpanded", true, false); settingsRoot.UpdateLayout();
        SaveThemePreview(settingsRoot, folder + "/assistant.png");
        ((FrameworkElement)((FrameworkElement)settings.FindName("SpeechApiKeyBox")).Parent).BringIntoView(); PumpAnimations(30); settingsRoot.UpdateLayout();
        SaveThemePreview(settingsRoot, folder + "/speech-key.png");
        PrivateCall(settings, "SelectPageByTag", "notifications"); settings.SettingsContentScroll.ScrollToHome(); settingsRoot.UpdateLayout();
        SaveThemePreview(settingsRoot, folder + "/notifications.png");
        var api = new ApiSettingsView(aiSecretsFile: Path.Combine(folder, "fixture-secrets.dat"));
        using (var host = new HwndSource(new HwndSourceParameters("AI settings layout preview")
        { Width = 560, Height = 680, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 }))
        {
            host.RootVisual = api; api.UpdateLayout(); SaveThemePreview(api, folder + "/ai-settings.png"); host.RootVisual = null;
        }
        PrivateCall(main, "SetState", IslandState.Card, true, "weather"); PumpAnimations(50); root.UpdateLayout();
        SaveThemePreview(main.IslandBody, folder + "/weather.png");
    }
}
