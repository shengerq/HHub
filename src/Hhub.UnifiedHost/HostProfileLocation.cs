namespace Hhub.UnifiedHost;

public static class HostProfileLocation
{
    public const string FileName = "install-profile.txt";
    public static string Resolve(string installationDirectory, string portableDefault)
    {
        string path = Path.Combine(installationDirectory, FileName);
        if (!File.Exists(path)) return Path.GetFullPath(portableDefault);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4096)
            throw new InvalidDataException("installation.profile-invalid");
        string value = File.ReadAllText(path).Trim();
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("installation.profile-invalid");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        string installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        if (root == Path.GetPathRoot(root) || IsWithin(root, installation) || IsWithin(installation, root))
            throw new InvalidDataException("installation.profile-overlap");
        return root;
    }
    private static bool IsWithin(string child, string parent) => string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
