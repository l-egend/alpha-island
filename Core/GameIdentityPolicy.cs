using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace alpha_island.Core;

internal sealed record GameForeground(uint ProcessId, string Path, string Title, bool ExclusiveDirect3D = false);
internal sealed record GameIdentity(uint ProcessId, string Path, string Name, string Source);

/// <summary>Exact game identities; fullscreen geometry alone is never evidence of a game.</summary>
internal sealed class GameIdentityPolicy
{
    private readonly HashSet<string> _windowsGames;
    private readonly string[] _custom;
    private static readonly HashSet<string> CommonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "YuanShen.exe", "GenshinImpact.exe", "StarRail.exe", "ZenlessZoneZero.exe", "Endfield.exe",
        "ForzaHorizon4.exe", "ForzaHorizon5.exe", "cs2.exe", "r5apex.exe", "r5apex_dx12.exe",
        "VALORANT-Win64-Shipping.exe", "League of Legends.exe", "Overwatch.exe", "RainbowSix.exe",
        "RainbowSix_Vulkan.exe", "GTA5.exe", "GTA5_Enhanced.exe", "Cyberpunk2077.exe", "eldenring.exe",
        "BlackMythWukong.exe", "b1-Win64-Shipping.exe", "Minecraft.Windows.exe", "Terraria.exe",
        "Stardew Valley.exe", "dota2.exe", "hl2.exe", "left4dead2.exe", "osu!.exe", "HTGame.exe",
        "DeltaForceClient-Win64-Shipping.exe", "TslGame.exe", "FortniteClient-Win64-Shipping.exe"
    };
    private static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe",
        "RiotClientServices.exe", "LeagueClient.exe", "LeagueClientUx.exe", "UbisoftConnect.exe", "upc.exe",
        "launcher.exe", "HoYoPlay.exe", "wegame.exe", "TGP.exe", "完美世界竞技平台.exe", "EADesktop.exe", "Origin.exe", "XboxPcApp.exe", "GameBar.exe", "GameBarFTServer.exe",
        "CrosshairX.exe", "wallpaper64.exe", "wallpaper32.exe", "blender.exe", "Unity.exe", "UnrealEditor.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "vlc.exe", "mpv.exe", "PotPlayerMini64.exe",
        "PotPlayerMini.exe", "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe", "obs64.exe",
        "explorer.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "Code.exe", "devenv.exe",
        "alpha-island.exe", "Codex.exe", "ChatGPT.exe", "Antigravity.exe", "QQ.exe", "WeChat.exe", "Weixin.exe"
    };
    internal GameIdentityPolicy(IEnumerable<string> windowsGames, string customProcesses = "")
    {
        _windowsGames = new(windowsGames.Where(IsAbsoluteExe), StringComparer.OrdinalIgnoreCase);
        _custom = DesktopNotificationPolicy.Tokens(customProcesses);
    }
    internal static bool IsAbsoluteExe(string path) => !string.IsNullOrWhiteSpace(path)
        && System.IO.Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    internal GameIdentity? Identify(GameForeground foreground)
    {
        if (foreground.ProcessId == 0 || !IsAbsoluteExe(foreground.Path)) return null;
        string exe = System.IO.Path.GetFileName(foreground.Path);
        string source;
        if (_custom.Any(token => IsAbsoluteExe(token) ? token.Equals(foreground.Path, StringComparison.OrdinalIgnoreCase)
            : (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? token : token + ".exe").Equals(exe, StringComparison.OrdinalIgnoreCase)))
            source = "用户指定";
        else if (NonGames.Contains(exe)) return null;
        else if (_windowsGames.Contains(foreground.Path)) source = "Windows 游戏记录";
        else if (CommonGames.Contains(exe)) source = "已知游戏进程";
        else if (foreground.ExclusiveDirect3D) source = "独占 Direct3D";
        else return null;
        string name = string.IsNullOrWhiteSpace(foreground.Title) ? System.IO.Path.GetFileNameWithoutExtension(exe) : foreground.Title.Trim();
        return new(foreground.ProcessId, foreground.Path, name.Length > 80 ? name[..80] : name, source);
    }
}