using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Collections.Generic;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    private static readonly Dictionary<string,(string Hash,Assembly Assembly)> WpfAssemblies=new(StringComparer.OrdinalIgnoreCase);
    internal PluginLoadContext(string assembly,bool collectible=true) : base(isCollectible: collectible)
    { _resolver = new AssemblyDependencyResolver(assembly); _directory = Path.GetDirectoryName(assembly)!; }
    internal Assembly LoadPluginAssembly(string path,bool wpfResources=false)
    {
        if(!wpfResources)return LoadManaged(path);
        // WPF caches pack resources by assembly name, outside collectible load contexts.
        // Reuse the same identity on enable/disable; stream loading still leaves files removable.
        string name=AssemblyName.GetAssemblyName(path).Name!;
        string hash;using(var file=File.OpenRead(path))hash=Convert.ToHexString(SHA256.HashData(file));
        if(WpfAssemblies.TryGetValue(name,out var cached))
        {
            if(cached.Hash!=hash)throw new InvalidOperationException("WPF 插件文件已更新，请重启应用后启用。");
            return cached.Assembly;
        }
        var existing=System.Linq.Enumerable.FirstOrDefault(Default.Assemblies,a=>a.GetName().Name==name);
        var assembly=existing??new PluginLoadContext(path,collectible:false).LoadManaged(path);
        WpfAssemblies[name]=(hash,assembly);return assembly;
    }
    private Assembly LoadManaged(string path)
    {
        // WPF 类型缓存可能延后回收上下文。短时读取托管 DLL，避免该缓存同时锁住磁盘文件。
        using var stream = File.OpenRead(path);
        string symbols = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(symbols)) return LoadFromStream(stream);
        using var pdb = File.OpenRead(symbols);
        return LoadFromStream(stream, pdb);
    }
    protected override Assembly? Load(AssemblyName name)
    {
        // SDK 始终共用宿主的类型身份，不能从插件副本再加载一份。
        if (name.Name == typeof(IIslandPlugin).Assembly.GetName().Name) return typeof(IIslandPlugin).Assembly;
        if (name.Name == typeof(alpha_island.Services.ChatService).Assembly.GetName().Name) return typeof(alpha_island.Services.ChatService).Assembly;
        if (name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Xaml"
            or "Microsoft.Windows.SDK.NET" or "WinRT.Runtime") return null;
        string? path = _resolver.ResolveAssemblyToPath(name);
        if (path == null && name.Name != null)
        {
            var adjacent = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(adjacent)) path = adjacent;
        }
        return path == null ? null : LoadManaged(path);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
