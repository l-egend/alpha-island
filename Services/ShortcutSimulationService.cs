using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using alpha_island.Interop;

namespace alpha_island.Services;

internal readonly record struct ShortcutSendResult(bool Sent, string Message);

internal interface IShortcutInputTransport
{
    bool IsKeyDown(int virtualKey);
    uint Send(NativeMethods.INPUT[] inputs);
}

/// <summary>One explicit shortcut invocation; never registers hotkeys or accesses the clipboard.</summary>
internal sealed class ShortcutSimulationService
{
    private static readonly SemaphoreSlim Sending = new(1, 1);
    private readonly IShortcutInputTransport _input;

    internal ShortcutSimulationService(IShortcutInputTransport? input = null) => _input = input ?? new WindowsInput();

    internal static bool TryParse(string? text, out HotkeyCombo shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return false;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "ALT" => ModifierKeys.Alt,
                "SHIFT" => ModifierKeys.Shift,
                "WIN" or "WINDOWS" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
            if (modifier == ModifierKeys.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }
        string keyText = parts[^1];
        if (keyText.Length == 1 && keyText[0] is >= '0' and <= '9') keyText = "D" + keyText;
        keyText = keyText.ToUpperInvariant() switch
        {
            "ENTER" => "Return", "ESC" => "Escape", "PGUP" => "PageUp", "PGDN" => "PageDown", _ => keyText
        };
        if (!Enum.TryParse<Key>(keyText, true, out var key) || !IsSupported(new(modifiers, key))) return false;
        // Enum.TryParse accepts numeric enum values; shortcut configuration must use named keys.
        if (keyText.Length > 1 && char.IsDigit(keyText[0])) return false;
        shortcut = new(modifiers, key);
        return true;
    }

    internal static bool IsSupported(HotkeyCombo shortcut)
    {
        if ((shortcut.Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) != 0) return false;
        Key key = shortcut.Key;
        bool function = key is >= Key.F1 and <= Key.F24;
        bool supported = function || key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Space or Key.Tab or Key.Return or Key.Escape or Key.Back or Key.Delete or Key.Insert
            or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal
            or Key.OemPlus or Key.OemMinus or Key.OemComma or Key.OemPeriod;
        return supported && (shortcut.Modifiers != ModifierKeys.None || function)
            && !(key == Key.Delete && shortcut.Modifiers.HasFlag(ModifierKeys.Control | ModifierKeys.Alt));
    }

    internal static string Format(HotkeyCombo shortcut)
    {
        var parts = new List<string>();
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(shortcut.Key is >= Key.D0 and <= Key.D9 ? ((int)shortcut.Key - (int)Key.D0).ToString()
            : shortcut.Key == Key.Return ? "Enter" : shortcut.Key.ToString());
        return string.Join("+", parts);
    }

    internal async Task<ShortcutSendResult> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!TryParse(text, out var shortcut))
            return new(false, "快捷键格式无效，请使用 Ctrl+Alt+D、Ctrl+Shift+T 或 F8 等组合。");
        if (!await Sending.WaitAsync(0, cancellationToken)) return new(false, "正在发送快捷键，请稍后重试。");
        try
        {
            var watch = Stopwatch.StartNew();
            while (HasHeldKeys(shortcut))
            {
                if (watch.ElapsedMilliseconds >= 750) return new(false, "请先松开键盘按键，再调用翻译软件。");
                await Task.Delay(25, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = CreateInputs(shortcut);
            uint sent = _input.Send(inputs);
            if (sent == inputs.Length) return new(true, "已发送 " + Format(shortcut) + "；请在翻译软件中选择区域并查看译文。");
            // Only release keys whose down event was accepted and whose up event was not.
            var pressed = new List<NativeMethods.INPUT>();
            foreach (var input in inputs.Take((int)Math.Min(sent, (uint)inputs.Length)))
            {
                if ((input.u.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) == 0) pressed.Add(input);
                else pressed.RemoveAll(p => p.u.ki.wVk == input.u.ki.wVk);
            }
            if (pressed.Count > 0)
            {
                pressed.Reverse();
                _input.Send(pressed.Select(p => KeyInput(p.u.ki.wVk, true)).ToArray());
            }
            return new(false, "快捷键发送失败；请检查翻译软件是否运行，以及两边的权限级别是否一致。");
        }
        finally { Sending.Release(); }
    }

    private bool HasHeldKeys(HotkeyCombo shortcut) => new[]
    {
        NativeMethods.VK_CONTROL, NativeMethods.VK_MENU, NativeMethods.VK_SHIFT,
        NativeMethods.VK_LWIN, NativeMethods.VK_RWIN, KeyInterop.VirtualKeyFromKey(shortcut.Key), 0x0D, 0x20
    }.Any(_input.IsKeyDown);

    internal static NativeMethods.INPUT[] CreateInputs(HotkeyCombo shortcut)
    {
        if (!IsSupported(shortcut)) throw new ArgumentException("Unsupported shortcut.", nameof(shortcut));
        var keys = new List<ushort>();
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Control)) keys.Add(NativeMethods.VK_CONTROL);
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Alt)) keys.Add(NativeMethods.VK_MENU);
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Shift)) keys.Add(NativeMethods.VK_SHIFT);
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Windows)) keys.Add(NativeMethods.VK_LWIN);
        keys.Add((ushort)KeyInterop.VirtualKeyFromKey(shortcut.Key));
        return keys.Select(k => KeyInput(k, false)).Concat(keys.AsEnumerable().Reverse().Select(k => KeyInput(k, true))).ToArray();
    }

    private static NativeMethods.INPUT KeyInput(ushort key, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT
        {
            wVk = key,
            dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x6F ? 1u : 0u),
            dwExtraInfo = NativeMethods.ExternalShortcutInputTag
        } }
    };

    private sealed class WindowsInput : IShortcutInputTransport
    {
        public bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        public uint Send(NativeMethods.INPUT[] inputs) => NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
