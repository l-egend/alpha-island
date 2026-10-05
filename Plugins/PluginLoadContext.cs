using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    internal PluginLoadContext(string assembly) : base(isCollectible: true)
    { _resolver = new AssemblyDependencyResolver(assembly); _directory = Path.GetDirectoryName(assembly)!; }
    internal Assembly LoadPluginAssembly(string path) => LoadManaged(path);
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
