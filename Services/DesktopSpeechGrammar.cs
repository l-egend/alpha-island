using System.Globalization;
using System.Speech.Recognition;

namespace alpha_island.Services;

internal static class DesktopSpeechGrammar
{
    internal static Grammar? Create(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName != "zh") return null;
        var volume = new GrammarBuilder { Culture = culture };
        volume.Append(new Choices("把音量调到", "将音量调到", "音量调到", "设置音量为", "把音量设置为"));
        volume.AppendDictation();
        var reminder = new GrammarBuilder { Culture = culture };
        reminder.Append(new Choices("提醒我", "帮我设置提醒", "设置提醒", "帮我设置番茄钟", "设置番茄钟", "开始番茄钟"));
        reminder.AppendDictation();
        var command = new Choices("静音", "取消静音", "暂停音乐", "暂停播放", "暂停播放音乐", "继续播放", "继续播放音乐", "播放下一首", "播放上一首", "下一首", "上一首", "停止番茄钟");
        command.Add(volume); command.Add(reminder);
        var builder = new GrammarBuilder { Culture = culture };
        builder.Append(new Choices("请", "请帮我", "帮我"), 0, 1); builder.Append(command);
        return new Grammar(builder) { Name = "desktop-command-vocabulary", Priority = 5 };
    }
}
