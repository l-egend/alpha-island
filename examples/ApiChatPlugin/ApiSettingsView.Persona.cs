using System.Diagnostics;
using System.IO;
using System.Windows;
using alpha_island.Conversations;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView
{
    private ApiPersonaLibrary? _personas;
    private string _personaFile = "";
    internal Func<string?>? PersonaFilePicker { get; set; }
    private void LoadPersona(IChatSettings settings)
    {
        AiSystemPromptBox.Text = ApiAssistantPrompt.PersonaFrom(settings);
        _personaFile = Path.GetFileName(settings.GetText("AiPersonaFile", ""));
        _personas = _context == null ? null : new(Path.Combine(_context.ConfigDirectory, "ai", "personas"));
        AiPersonaImportButton.IsEnabled = AiPersonaFolderButton.IsEnabled = _personas != null;
        UpdatePersonaInfo();
    }
    private void UpdatePersonaInfo(string? error = null)
    {
        if (AiPersonaStatusText == null) return;
        AiPersonaStatusText.Text = error ?? $"{(_personaFile.Length == 0 ? "当前人格" : _personaFile)} · {AiSystemPromptBox.Text.Length}/{ApiAssistantPrompt.MaximumPersonaCharacters} 字符（会话首次发送）";
        AiPersonaStatusText.ToolTip = error ?? "人格仅在会话首次成功请求时附加；修改后新建或清空对话生效。普通无状态接口后续只参考聊天历史，不能保证完整记住人格。内置规则每次发送。编辑不覆盖原文件，修改文件后请重新导入。";
    }
    internal void ImportPersonaFromFile(string path)
    {
        if (_personas == null) throw new InvalidOperationException("请在已加载的 API 对话插件中导入人格。");
        // Validate and copy before changing the active personality, so failures leave the previous one intact.
        var imported = _personas.Import(path);
        bool previous = _suppress; _suppress = true;
        try { AiSystemPromptBox.Text = imported.Text; _personaFile = imported.FileName; }
        finally { _suppress = previous; }
        UpdatePersonaInfo(); ApplyChanges();
    }
    private void ImportPersona_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_personas == null) return;
            _personas.EnsureDirectory();
            string? file;
            if (PersonaFilePicker != null) file = PersonaFilePicker();
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入人格提示词", Filter = "人格提示词 (*.txt)|*.txt",
                    InitialDirectory = _personas.Root, CheckFileExists = true, Multiselect = false };
                file = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            }
            if (file != null) ImportPersonaFromFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { UpdatePersonaInfo("导入失败：" + ex.Message); }
    }
    private void OpenPersonaFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_personas == null) return;
            _personas.EnsureDirectory(); Process.Start(new ProcessStartInfo(_personas.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { UpdatePersonaInfo("无法打开人格文件夹：" + ex.Message); }
    }
}
