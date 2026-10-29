using System.IO;
using System.Reflection;

internal static partial class Program
{
    private static string LocalDesktopTool(string name)
    {
        if (Path.GetFileName(name) != name) throw new ArgumentException("A tool filename is required.", nameof(name));
        string root = Environment.GetEnvironmentVariable("ALPHA_ISLAND_LOCAL_TOOLS")
            ?? Path.GetFullPath(Path.Combine("..", "local-tools", "host"));
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (!File.Exists(path)) throw new FileNotFoundException(
            "This optional live check requires workstation tooling. Set ALPHA_ISLAND_LOCAL_TOOLS to its host directory.", name);
        return path;
    }

    private static string ExternalPluginSource(string relative)
    {
        string root = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ExternalPluginSourceRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "examples", relative));
    }
}
