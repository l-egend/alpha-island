using System.IO;
using System.Reflection;

internal static partial class Program
{
    private static string ExternalPluginSource(string relative)
    {
        string root = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ExternalPluginSourceRoot").Value!;
        return Path.GetFullPath(Path.Combine(root, "examples", relative));
    }
}
