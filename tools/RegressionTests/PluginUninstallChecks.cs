using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Settings;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

internal static partial class Program
{
    private static void PluginUninstallChecks(alpha_island.MainWindow window, string testRoot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pages = (IslandPageHost)typeof(alpha_island.MainWindow).GetField("_pages", flags)!.GetValue(window)!;
        string previous = pages.ActiveId;
        string pluginRoot = Path.Combine(testRoot, "uninstall-plugins"); Directory.CreateDirectory(pluginRoot);
        string WriteProbe(string folder, string id)
        {
            string path = Path.Combine(pluginRoot, folder); Directory.CreateDirectory(path);
            File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Debug/net8.0-windows/AlphaIsland.PluginFixture.dll"),
                Path.Combine(path, "AlphaIsland.PluginFixture.dll"));
            File.WriteAllText(Path.Combine(path, "plugin.json"), JsonSerializer.Serialize(new
            { id, name = id, version = "1.0.0", apiVersion = 1, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.ProbePlugin" }));
            return path;
        }
        string probeFolder = WriteProbe("a-probe", "fixture.uninstall");
        string siblingFolder = WriteProbe("b-sibling", "fixture.sibling");
        string lockedFolder = WriteProbe("c-locked", "fixture.locked");
        string brokenFolder = Path.Combine(pluginRoot, "z-broken"); Directory.CreateDirectory(brokenFolder);
        File.WriteAllText(Path.Combine(brokenFolder, "plugin.json"), "invalid json");
        string readOnlyAsset = Path.Combine(brokenFolder, "assets", "read-only.txt"); Directory.CreateDirectory(Path.GetDirectoryName(readOnlyAsset)!);
        File.WriteAllText(readOnlyAsset, "asset"); File.SetAttributes(readOnlyAsset, FileAttributes.ReadOnly);
        var options = new IslandOptions();
        foreach (string id in new[] { "fixture.uninstall", "fixture.sibling", "fixture.locked" })
            options.Extras[PluginHost.EnabledKey(id)] = JsonSerializer.SerializeToElement(true);
        options.Extras[PluginHost.SettingKey("fixture.uninstall", "number")] = JsonSerializer.SerializeToElement(7);
        options.Extras[PluginHost.SettingKey("fixture.uninstall-other", "keep")] = JsonSerializer.SerializeToElement("keep");
        using var host = new PluginHost(window.Dispatcher, options, new[] { pluginRoot },
            page => { window.CardViewport.Children.Add(page.View); pages.Register(page); },
            page => { PrivateCall(window, "RemoveCardPinLayer", page.Id); pages.Unregister(page.Id); window.CardViewport.Children.Remove(page.View); },
            id => PrivateCall(window, "SelectComponent", id));
        var settings = new SettingsWindow(persistSettings: false) { ForceClose = true };
        try
        {
            host.Discover();
            var probe = host.Entries.Single(e => e.Manifest?.Id == "fixture.uninstall");
            var sibling = host.Entries.Single(e => e.Manifest?.Id == "fixture.sibling");
            var locked = host.Entries.Single(e => e.Manifest?.Id == "fixture.locked");
            var instance = probe.Instance!; var lifetime = probe.Context!.Lifetime; string pageId = probe.Pages.Single().Id;
            settings.LoadFrom(options); settings.BindPluginHost(host);
            settings.OptionsChanged += (_, updated) => { options.CopyFrom(updated); host.Sync(options); };
            var rows = (StackPanel)settings.FindName("PluginRows");
            Button UninstallButton(string folder) => Descendants<Button>(rows).Single(b => Equals(b.Tag, "plugin-uninstall:" + folder));
            void ClickUninstall(string folder) { UninstallButton(folder).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(30); }
            Check(Descendants<Button>(rows).Count(b => b.Tag is string tag && tag.StartsWith("plugin-uninstall:")) == 4,
                "运行、停用及损坏插件均提供卸载按钮");
            ClickUninstall(probeFolder);
            Check(!Directory.Exists(probeFolder) && !host.Entries.Contains(probe) && !probe.Running
                && lifetime.IsCancellationRequested && pages.IndexOf(pageId) < 0,
                "真实按钮一键删除运行中的托管插件、取消任务并撤下卡片");
            Check((bool)instance.GetType().GetProperty("Stopped")!.GetValue(instance)!
                && (bool)instance.GetType().GetProperty("Disposed")!.GetValue(instance)!, "卸载调用服务 Stop 和插件 Dispose");
            Check(!options.Extras.Keys.Any(k => k.StartsWith("plugin/fixture.uninstall/"))
                && options.Extras[PluginHost.SettingKey("fixture.uninstall-other", "keep")].GetString() == "keep"
                && sibling.Running && Directory.Exists(siblingFolder), "卸载只清除本插件设置，保留相近 Id 和其他运行插件");
            Check(!Descendants<Button>(rows).Any(b => Equals(b.Tag, "plugin-uninstall:" + probeFolder)), "卸载成功立即从设置列表移除");
            Check(!host.Uninstall(probe, out var staleError) && staleError.Length > 0 && sibling.Running,
                "重复或过期卸载请求不会删除其他插件");

            string lockedFile = Path.Combine(lockedFolder, "busy.bin"); File.WriteAllText(lockedFile, "busy");
            using (var held = File.Open(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ClickUninstall(lockedFolder);
                Check(!locked.Running && locked.UninstallPending && locked.UninstallError.Length > 0
                    && !PluginHost.IsEnabled(options, "fixture.locked") && File.Exists(Path.Combine(lockedFolder, "plugin.json")),
                    "文件占用时停用插件并保留清单和卸载失败信息");
                Check(Equals(UninstallButton(lockedFolder).Content, "重试卸载")
                    && !Descendants<CheckBox>(rows).Single(c => Equals(c.Tag, PluginHost.EnabledKey("fixture.locked"))).IsEnabled
                    && Descendants<TextBlock>(rows).Any(t => t.Text.StartsWith("卸载失败：")), "删除失败显示原因和重试按钮，避免启动部分删除的插件");
                host.Discover(); PumpAnimations(30); host.Sync(options);
                Check(host.Entries.Contains(locked) && !locked.Running && sibling.Running, "刷新插件列表保留失败重试入口，其他插件正常运行");
            }
            ClickUninstall(lockedFolder);
            Check(!Directory.Exists(lockedFolder) && !host.Entries.Contains(locked)
                && !options.Extras.ContainsKey(PluginHost.EnabledKey("fixture.locked")), "释放占用后重试可完成删除和设置清理");
            ClickUninstall(brokenFolder);
            Check(!Directory.Exists(brokenFolder) && host.Entries.Count == 1 && sibling.Running, "损坏清单及只读嵌套资产也能一键卸载");

            string outside = Path.Combine(testRoot, "outside-plugins"); Directory.CreateDirectory(outside);
            string sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "keep");
            var entries = (List<PluginHost.Entry>)typeof(PluginHost).GetField("_entries", flags)!.GetValue(host)!;
            var outsideEntry = new PluginHost.Entry { Directory = outside }; entries.Add(outsideEntry);
            Check(!host.Uninstall(outsideEntry, out var outsideError) && outsideError.Length > 0 && File.ReadAllText(sentinel) == "keep",
                "卸载拒绝插件根目录之外的目标"); entries.Remove(outsideEntry);
            var rootEntry = new PluginHost.Entry { Directory = pluginRoot }; entries.Add(rootEntry);
            Check(!host.Uninstall(rootEntry, out _) && Directory.Exists(pluginRoot) && Directory.Exists(siblingFolder),
                "卸载拒绝插件根目录本身，保留其他插件目录"); entries.Remove(rootEntry);

            string link = Path.Combine(pluginRoot, "z-junction");
            // 此命令只创建测试根目录内的目录联接；删除联接使用 Directory.Delete 的非递归模式。
            using (var create = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!)
            {
                create.WaitForExit();
                if (create.ExitCode != 0) throw new IOException("Cannot create test junction: " + create.StandardError.ReadToEnd());
            }
            try
            {
                var linkedEntry = new PluginHost.Entry { Directory = link }; entries.Add(linkedEntry);
                Check(!host.Uninstall(linkedEntry, out var linkError) && linkError.Contains("链接")
                    && File.ReadAllText(sentinel) == "keep" && Directory.Exists(link), "卸载拒绝目录联接，保护链接目标文件");
                entries.Remove(linkedEntry);
            }
            finally { Directory.Delete(link); }
            host.Discover();
            Check(host.Entries.Count == 1 && sibling.Running && Directory.Exists(siblingFolder), "卸载后刷新不会重新发现已删除插件");
            var restored = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!;
            Check(!restored.Extras.Keys.Any(k => k.StartsWith("plugin/fixture.uninstall/")) && PluginHost.IsEnabled(restored, "fixture.sibling"),
                "卸载后的设置序列化只移除目标插件，保留其他插件开关");
        }
        finally
        {
            settings.Close(); host.Dispose(); pages.SetActiveIndex(pages.IndexOfOrDefault(previous));
            PrivateCall(window, "SwitchCardPage", pages.ActiveIndex, false, false);
        }
    }
}
