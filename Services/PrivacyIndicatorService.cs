using System;
using System.Collections.Generic;
using System.IO;
using alpha_island.Core;

namespace alpha_island.Services
{
    /// <summary>隐私能力（麦克风 / 摄像头）。</summary>
    public enum PrivacyCapability
    {
        Microphone,
        Camera
    }

    /// <summary>一条「某个应用正在使用麦克风/摄像头」的记录。</summary>
    public readonly record struct PrivacyUsage(PrivacyCapability Capability, string AppName, string SourceKey);

    /// <summary>
    /// 麦克风 / 摄像头占用检测。
    ///
    /// 原理：Windows 在
    ///   HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{microphone|camera}
    /// 下为每个用过设备的应用记录一对 FILETIME（100ns，1601 起算）：
    ///   - <c>LastUsedTimeStart</c>：本次开始使用时刻
    ///   - <c>LastUsedTimeStop</c> ：本次结束时刻；**还在用就是 0**（或小于 Start）
    /// 所以「正在使用」= <c>Stop == 0 || Stop &lt; Start</c>。
    ///
    /// 子键格式示例：
    ///   - `microphone\NonPackaged\C:#Program Files#ExampleApp#example.exe`（非打包应用，路径里的 `\` 写成 `#`）
    ///   - `microphone\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe`（打包应用，直接挂在能力键下面）
    ///   - 某些系统/版本上打包应用会挂在 `Packaged\` 子键里 —— 两种都认
    ///   - `camera` 键不存在时按无使用记录处理。
    ///
    /// 只读系统使用记录，不请求麦克风或摄像头采集权限。
    /// 自己不开定时器：节拍由 <c>IslandTicker</c> 给。
    /// </summary>
    public sealed class PrivacyIndicatorService
    {
        private const string ConsentStorePath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

        public string? DegradedReason { get; private set; }
        private IReadOnlyList<PrivacyUsage> _lastSuccessful = Array.Empty<PrivacyUsage>();

        /// <summary>扫一遍注册表，返回当前正在使用麦克风/摄像头的应用。</summary>
        public IReadOnlyList<PrivacyUsage> Sample()
        {
            var result = new List<PrivacyUsage>();
            DegradedReason = null;
            var nowUtc = DateTime.UtcNow;

            Scan(result, Microsoft.Win32.Registry.CurrentUser, PrivacyCapability.Microphone, "microphone", nowUtc);
            Scan(result, Microsoft.Win32.Registry.CurrentUser, PrivacyCapability.Camera, "camera", nowUtc);
            Scan(result, Microsoft.Win32.Registry.LocalMachine, PrivacyCapability.Microphone, "microphone", nowUtc);
            Scan(result, Microsoft.Win32.Registry.LocalMachine, PrivacyCapability.Camera, "camera", nowUtc);

            if (DegradedReason != null) return _lastSuccessful;
            _lastSuccessful = result.DistinctBy(x => (x.Capability, x.SourceKey.ToUpperInvariant())).ToArray();
            return _lastSuccessful;
        }

        private void Scan(List<PrivacyUsage> result, Microsoft.Win32.RegistryKey root,
            PrivacyCapability capability, string capabilityName, DateTime nowUtc)
        {
            try
            {
                using var key = root.OpenSubKey($@"{ConsentStorePath}\{capabilityName}");
                if (key == null) return;   // 这台机器从没用过该设备

                foreach (string childName in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(childName);
                    if (child == null) continue;

                    if (childName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string exeKey in child.GetSubKeyNames())
                        {
                            using var exe = child.OpenSubKey(exeKey);
                            if (exe == null || !IsActive(exe, nowUtc)) continue;
                            result.Add(new PrivacyUsage(capability, NonPackagedDisplayName(exeKey), exeKey));
                        }
                    }
                    else if (childName.Equals("Packaged", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string packageKey in child.GetSubKeyNames())
                        {
                            using var package = child.OpenSubKey(packageKey);
                            if (package == null || !IsActive(package, nowUtc)) continue;
                            result.Add(new PrivacyUsage(capability, PackagedDisplayName(packageKey), packageKey));
                        }
                    }
                    else
                    {
                        // 打包应用直接挂在能力键下面
                        if (IsActive(child, nowUtc))
                        {
                            result.Add(new PrivacyUsage(capability, PackagedDisplayName(childName), childName));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DegradedReason = "部分设备占用状态读取失败，保留上次结果";
                IslandLog.Warn("Privacy", $"读取 {capabilityName} 占用状态失败: {ex.Message}");
            }
        }

        /// <summary>Stop == 0（还在用）或 Stop &lt; Start（回写乱序）都算正在使用。</summary>
        private static bool IsActive(Microsoft.Win32.RegistryKey key, DateTime nowUtc)
        {
            long start = ReadQword(key, "LastUsedTimeStart");
            long stop = ReadQword(key, "LastUsedTimeStop");
            return IsActive(start, stop);
        }

        internal static bool IsActive(long start, long stop) => start > 0 && (stop == 0 || stop < start);

        private static long ReadQword(Microsoft.Win32.RegistryKey key, string name)
        {
            object? value = key.GetValue(name);
            return value switch
            {
                long l => l,
                int i => i,
                byte[] bytes when bytes.Length == 8 => BitConverter.ToInt64(bytes, 0),
                _ => 0
            };
        }

        private static DateTime FromFileTimeUtc(long fileTime)
        {
            try
            {
                return DateTime.FromFileTimeUtc(fileTime);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>`C:#Program Files#Tencent#QQNT#QQ.exe` → `QQ`</summary>
        private static string NonPackagedDisplayName(string registryKeyName)
        {
            string path = registryKeyName.Replace('#', Path.DirectorySeparatorChar);
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? registryKeyName : name;
        }

        /// <summary>`com.flutter.kazumi_wbnnev551gwxy` → `kazumi`；`Microsoft.WindowsSoundRecorder_8we…` → `SoundRecorder`</summary>
        private static string PackagedDisplayName(string packageFamilyName)
        {
            string trimmed = packageFamilyName;
            int underscore = trimmed.IndexOf('_');
            if (underscore > 0) trimmed = trimmed[..underscore];   // 去掉 publisher hash

            int dot = trimmed.LastIndexOf('.');
            if (dot >= 0 && dot < trimmed.Length - 1) trimmed = trimmed[(dot + 1)..];

            return string.IsNullOrWhiteSpace(trimmed) ? packageFamilyName : trimmed;
        }
    }
}
