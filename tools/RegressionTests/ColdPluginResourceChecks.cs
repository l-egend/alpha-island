using System.Windows.Interop;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Plugins;

internal static partial class Program
{
    // No ApiChat type reference: exercise the same first load context as the installed application.
    private static void ColdPluginResourceChecks()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"alpha-island-cold-plugin-"+Guid.NewGuid().ToString("N"));
            try
            {
                Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="AlphaIsland.Plugins.ApiChat"),"冷启动探针尚未在默认上下文加载 API 插件");
                var app=new System.Windows.Application();
                foreach(string theme in new[]{"Controls","LiquidGlass","Win11Settings"})
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/alpha-island;component/Themes/"+theme+".xaml")});
                string plugins=Path.Combine(root,"plugins");string directory=Path.Combine(plugins,"ai.api-chat");Directory.CreateDirectory(directory);
                File.Copy(ExternalPluginSource("ApiChatPlugin/bin/Release/net8.0-windows/AlphaIsland.Plugins.ApiChat.dll"),Path.Combine(directory,"AlphaIsland.Plugins.ApiChat.dll"));
                File.Copy(ExternalPluginSource("ApiChatPlugin/plugin.json"),Path.Combine(directory,"plugin.json"));
                var options=new IslandOptions();var panel=new Grid();
                using var host=new PluginHost(Dispatcher.CurrentDispatcher,options,new[]{plugins},p=>panel.Children.Add(p.View),p=>panel.Children.Remove(p.View),_=>{}){DataRoot=Path.Combine(root,"data")};
                host.Discover();var entry=host.Entries.Single();
                using var surface = new HwndSource(new HwndSourceParameters("Cold remote settings") { Width = 700, Height = 700, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000) }); surface.RootVisual = panel;
                for(int cycle=0;cycle<4;cycle++)
                {
                    PumpUntilState(() => entry.Running || entry.Error.Length > 0);
                    Check(entry.Running&&entry.Error.Length==0,"冷启动及反复启用时 WPF 插件页面正常加载 "+cycle);
                    var settings=entry.Context!.SettingsView!();settings.Measure(new System.Windows.Size(700,700));settings.Arrange(new Rect(0,0,700,700));settings.UpdateLayout(); panel.Children.Add(settings);
                    PumpUntilState(() => settings.FindName("ManageAiHistoryButton") is System.Windows.Controls.Button);
                    Check(settings.FindName("ManageAiHistoryButton") is System.Windows.Controls.Button,"独立加载的 XAML 设置页可解析并布局 "+cycle);
                    panel.Children.Remove(settings); options.Extras[PluginHost.EnabledKey("ai.api-chat")]=JsonSerializer.SerializeToElement(false);host.Sync(options); AwaitPlugin(entry.Teardown.ContinueWith(_ => true));
                    options.Extras[PluginHost.EnabledKey("ai.api-chat")]=JsonSerializer.SerializeToElement(true);host.Sync(options);
                }
                Check(AwaitPlugin(host.UninstallAsync(entry))&&!Directory.Exists(directory),"真实 WPF 插件资源缓存不锁定 DLL，仍可删除插件目录");
                app.Shutdown();
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                if(Directory.Exists(root)&&Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(root,true);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
