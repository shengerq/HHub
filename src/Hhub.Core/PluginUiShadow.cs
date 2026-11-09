using System.Security.Cryptography;
using System.Text;

namespace Hhub.Core;

public static class PluginUiShadow
{
    // Keep a module's sibling resources beside Assembly.Location, while loading
    // immutable copies so an open UI cannot lock the installed package's DLL.
    public static string Stage(InstalledPlugin plugin, string cacheRoot)
    {
        string assembly = PluginCatalog.ResolveInside(plugin.Directory, plugin.Manifest.UiAssembly
            ?? throw new InvalidDataException("插件未声明界面模块"));
        string source = Path.GetDirectoryName(assembly)!;
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        foreach (string file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(source, file) + "\0"));
            using var stream = File.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        string destination = Path.Combine(cacheRoot, plugin.Manifest.Id, Convert.ToHexString(hash.GetHashAndReset()));
        string result = Path.Combine(destination, Path.GetFileName(assembly));
        if (File.Exists(Path.Combine(destination, ".__hhub_complete")) && File.Exists(result)) return result;
        Directory.CreateDirectory(destination);
        foreach (string file in files)
        {
            string copy = PluginCatalog.ResolveInside(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }
        File.WriteAllText(Path.Combine(destination, ".__hhub_complete"), "");
        return result;
    }
}
